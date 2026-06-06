# Magentic Orchestration — OpenTelemetry Span Schema

**Status:** Draft. Aligned to OTel Semantic Conventions **1.41.1** (GenAI sections marked *Development*; gated by `OTEL_SEMCONV_STABILITY_OPT_IN=gen_ai_latest_experimental`). MAF Magentic surface is marked *experimental* by Microsoft (`#pragma warning disable MAAIW001` in samples, even though MAF 1.0 GA shipped April 2026).

## Stability & API-churn risk

The Magentic orchestrator types live in `Microsoft.Agents.AI.Workflows` and `Microsoft.Agents.AI.Workflows.Specialized.Magentic`. The orchestrator class (`MagenticOrchestrator`) is `internal`; the **public surface we instrument against** is:

- `MagenticOrchestratorEvent` (abstract) and the three sealed `MagenticPlanCreatedEvent` / `MagenticReplannedEvent` / `MagenticProgressLedgerUpdatedEvent` derivatives.
- `MagenticProgressLedger` (public class with `IsRequestSatisfied`, `IsInLoop`, `IsProgressBeingMade`, `NextSpeaker`, `InstructionOrQuestion`, `IsStarted`).
- `MagenticPlanReviewRequest(Plan, CurrentProgress, IsStalled)` / `MagenticPlanReviewResponse`.
- `MagenticWorkflowBuilder` (`WithMaxRounds`, `WithMaxStalls`, `WithMaxResets`, `RequirePlanSignoff`).

Counters (`RoundCount`, `StallCount`, `ResetCount`) live on `MagenticTaskContext.TaskCounters` and are **internal** — instrumentation must derive their effective values from the public event stream (one progress-ledger event = one round; each `MagenticReplannedEvent` = one reset; stall is signaled by `IsStalled=true` on the `MagenticPlanReviewRequest` that precedes a replan).

## 1. Span tree overview

```
invoke_workflow magentic.{workflow_name}                       (INTERNAL, parent of the whole orchestration)
└─ invoke_agent MagenticManager                                (INTERNAL — orchestrator/manager span)
   ├─ chat {model}                                             (CLIENT — plan synthesis LLM call)
   ├─ gen_ai.orchestration.magentic.plan_review                (INTERNAL — only if RequirePlanSignoff=true)
   ├─ gen_ai.orchestration.magentic.round                      (INTERNAL — one per coordination round)
   │  ├─ chat {model}                                          (manager's progress-ledger LLM call)
   │  └─ invoke_agent {participant}                            (INTERNAL — dispatched participant)
   │     ├─ chat {model}
   │     └─ execute_tool {tool_name}                           (INTERNAL — participant tool calls)
   ├─ gen_ai.orchestration.magentic.reset                      (INTERNAL — emitted on stall-triggered reset)
   │  └─ chat {model}                                          (manager replan)
   └─ ... (rounds repeat)
```

`gen_ai.orchestration.magentic.*` is a harness-defined namespace because the OTel spec defines `invoke_workflow`, `invoke_agent`, `chat`, and `execute_tool` but **does not** model coordination-rounds, plan reviews, or replans. These extension spans are explicitly flagged as non-standard.

## 2. Span definitions

### 2.1 `invoke_workflow magentic.{workflow_name}`

| Field | Value |
|---|---|
| `gen_ai.operation.name` | `invoke_workflow` (Required) |
| Span kind | `INTERNAL` |
| Parent | none (root) or the caller's span |
| Start | `MagenticWorkflowBuilder.Build().RunAsync(...)` |
| End | terminal `WorkflowOutputEvent` or `WorkflowErrorEvent` |
| `gen_ai.workflow.name` | from `MagenticWorkflowBuilder.WithName(...)` (Conditionally Required when available) |
| `gen_ai.orchestration.magentic.max_rounds` | int? (harness ext) |
| `gen_ai.orchestration.magentic.max_stalls` | int (harness ext, default 3) |
| `gen_ai.orchestration.magentic.max_resets` | int? (harness ext) |
| `gen_ai.orchestration.magentic.require_plan_signoff` | bool (harness ext) |
| `gen_ai.orchestration.magentic.participants` | string[] of agent IDs (harness ext) |
| `gen_ai.orchestration.magentic.rounds_executed` | int, set at span end (harness ext) |
| `gen_ai.orchestration.magentic.resets_executed` | int, set at span end (harness ext) |
| `gen_ai.orchestration.magentic.completion_reason` | enum: `satisfied` | `round_limit` | `reset_limit` | `error` |
| `error.type` | only if failed |

### 2.2 `invoke_agent MagenticManager`

Wraps the manager agent's lifetime. Per OTel `invoke_agent` (internal flavor, `INTERNAL` kind, since the manager runs in-process).

| Field | Value |
|---|---|
| `gen_ai.operation.name` | `invoke_agent` (Required) |
| `gen_ai.agent.name` | from the manager's `AIAgent.Name` (e.g., `MagenticManager`) |
| `gen_ai.agent.id` | manager agent ID |
| `gen_ai.conversation.id` | workflow run ID |
| `gen_ai.orchestration.magentic.role` | `manager` (harness ext, distinguishes from participant spans) |

Inherits all `chat` child spans for LLM calls made to plan, replan, and update the progress ledger.

### 2.3 `gen_ai.orchestration.magentic.round` (extension span)

One per coordination round. Created when the orchestrator enters its inner loop; closed when the round resolves (either dispatched a participant, satisfied, or triggered reset).

| Field | Value |
|---|---|
| Span name | `magentic.round {round_number}` |
| Kind | `INTERNAL` |
| Parent | manager `invoke_agent` span |
| Start | `MagenticOrchestrator.CoordinateAsync` round entry (incrementing `RoundCount`) |
| End | round resolution |
| `gen_ai.orchestration.magentic.round.number` | int (1-based) |
| `gen_ai.orchestration.magentic.round.stall_count_after` | int (post-update; harness derives from progress-ledger event) |
| `gen_ai.orchestration.magentic.progress.is_request_satisfied` | bool (from `MagenticProgressLedger`) |
| `gen_ai.orchestration.magentic.progress.is_in_loop` | bool |
| `gen_ai.orchestration.magentic.progress.is_progress_being_made` | bool |
| `gen_ai.orchestration.magentic.progress.next_speaker` | string (participant name) |

Events on this span:
- `gen_ai.orchestration.magentic.progress_ledger_updated` (mirrors the public `MagenticProgressLedgerUpdatedEvent`).

If the round dispatches a participant, a child `invoke_agent {participant}` span is opened. That child runs in the participant's process flow, so it follows OTel `invoke_agent` *internal* span conventions verbatim — with `gen_ai.orchestration.magentic.role=participant` for filterability.

### 2.4 `gen_ai.orchestration.magentic.reset` (extension span)

Emitted when the orchestrator triggers reset+replan, either because `StallCount > MaxStallCount` or because the manager's progress-ledger update threw. Wraps the `ResetAndReplanAsync` call.

| Field | Value |
|---|---|
| Span name | `magentic.reset {reset_number}` |
| Kind | `INTERNAL` |
| Parent | manager `invoke_agent` span |
| `gen_ai.orchestration.magentic.reset.number` | int |
| `gen_ai.orchestration.magentic.reset.trigger` | enum: `stall` | `ledger_failure` |
| `gen_ai.orchestration.magentic.reset.was_stalled` | bool (mirrors `taskContext.IsStalled` at reset time) |

Closed when the new task ledger is in place; emits a `MagenticReplannedEvent` mirror event.

### 2.5 `gen_ai.orchestration.magentic.plan_review` (extension span)

Emitted only when `RequirePlanSignoff=true`. Wraps the pause from `SubmitPlanReviewRequestAsync` to the resumption with `MagenticPlanReviewResponse`.

| Field | Value |
|---|---|
| Span name | `magentic.plan_review` |
| Kind | `INTERNAL` |
| `gen_ai.orchestration.magentic.plan_review.outcome` | enum: `approved` | `revised` (derived from response `messages.Count == 0`) |
| `gen_ai.orchestration.magentic.plan_review.is_stalled` | bool (mirrors request) |
| `gen_ai.orchestration.magentic.plan_review.has_progress_ledger` | bool (`CurrentProgress` is non-null on stall-triggered reviews; null on initial) |
| Events | `gen_ai.orchestration.magentic.plan_review.requested`, `gen_ai.orchestration.magentic.plan_review.resolved` |

### 2.6 `chat {model}` (LLM calls)

Each manager and participant LLM call follows OTel **GenAI Spans / Inference** verbatim: `gen_ai.operation.name=chat`, `gen_ai.provider.name`, `gen_ai.request.model`, `gen_ai.response.model`, `gen_ai.response.finish_reasons`, `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`, `gen_ai.response.id`, `gen_ai.request.temperature`, `gen_ai.request.top_p`, etc. All `Recommended` per spec.

### 2.7 `execute_tool {tool_name}`

Participant tool calls follow OTel `execute_tool` verbatim: `gen_ai.operation.name=execute_tool` (Required), `gen_ai.tool.name` (Required), `gen_ai.tool.call.id`, `gen_ai.tool.description`, `gen_ai.tool.type`. Note: the manager agent in MAF Magentic **does not call tools** — only participants do.

## 3. Task Ledger representation

The outer Task Ledger is the manager's plan-of-record. It surfaces as:

- A `MagenticPlanCreatedEvent` (initial) or `MagenticReplannedEvent` (subsequent) carrying `FullTaskLedger : ChatMessage`.
- Represented in spans as a **span event** on the manager `invoke_agent` span: `gen_ai.orchestration.magentic.plan_created` / `gen_ai.orchestration.magentic.replanned`. Use a span event (not an attribute) because the plan content is potentially large and is opt-in capture.
- Stable attribute on the event: `gen_ai.orchestration.magentic.plan.version` (monotonic int — 1 for initial, +1 per replan).

## 4. Progress Ledger representation

The inner Progress Ledger surfaces once per coordination round. Represent it as **attributes on the `magentic.round` span** (low-cardinality booleans + `next_speaker` string), not as a span event, because every round emits exactly one ledger and the booleans drive sampling/filtering. The full structured ledger JSON goes on a span event when content capture is enabled.

## 5. Stall counter + replan

Stall is **not directly observable** from the public event stream — it lives on `MagenticTaskContext.TaskCounters.StallCount`. The harness derives effective stall semantics:

- A round whose progress ledger has `IsInLoop=true` or `IsProgressBeingMade=false` increments the harness-side stall counter; a clean round decrements it (floor 0). This mirrors the orchestrator's logic.
- When `StallCount > MaxStallCount`, the orchestrator opens a `magentic.reset` span with `trigger=stall, was_stalled=true`. The subsequent `MagenticReplannedEvent` closes the reset span and starts the new outer-loop iteration.
- Replans always open a new round-numbering generation. Surface this with `gen_ai.orchestration.magentic.plan.version` so timelines can group rounds under their governing plan.

## 6. HITL plan-review hook

When `RequirePlanSignoff=true`, the orchestrator emits a `MagenticPlanReviewRequest` and **pauses** the workflow. The `magentic.plan_review` span (§2.5) covers the entire pause:

- Span starts on `SubmitPlanReviewRequestAsync`.
- A `pending_request` event marks the suspension; downstream `SuperStepCompletedEvent` checkpoint info can be attached.
- Span ends on `ExternalResponse` arrival; `outcome` is `approved` if `MagenticPlanReviewResponse.Messages` is empty, otherwise `revised`.
- Pause duration is the span duration. Pair with a separate metric `gen_ai.orchestration.magentic.plan_review.duration` if SLOs care.

## 7. Content capture policy

Following OTel GenAI guidance ("OpenTelemetry instrumentations SHOULD NOT capture [model instructions, user messages, model outputs] by default, but SHOULD provide an option for users to opt in"), **all of the following are opt-in only**, gated by a single harness setting `Observability:Magentic:CaptureContent=false`:

| Attribute / Event content | Default | When enabled |
|---|---|---|
| `gen_ai.input.messages` / `gen_ai.output.messages` on `chat` spans | off | follow OTel JSON schema |
| `gen_ai.tool.call.arguments` / `gen_ai.tool.call.result` on `execute_tool` | off | follow OTel guidance |
| `gen_ai.orchestration.magentic.plan.content` on plan_created/replanned events | off | `FullTaskLedger.Text` |
| `gen_ai.orchestration.magentic.progress.instruction_or_question` on round span | off | `InstructionOrQuestion` string |
| `gen_ai.orchestration.magentic.replan.reason` on reset span | off | `WorkflowWarningEvent` payload when reset trigger is `ledger_failure` |
| `gen_ai.orchestration.magentic.plan_review.feedback` on plan_review span | off | revision text from `MagenticPlanReviewResponse` |

Metadata-only spans (counters, booleans, IDs, durations, finish reasons) are always emitted.

## 8. Sources

- OTel GenAI agent/framework spans (semconv 1.41.1, *Development*): https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-agent-spans/
- OTel GenAI client spans (`chat`, `execute_tool`, content-capture policy): https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-spans/
- OTel GenAI attribute registry: https://opentelemetry.io/docs/specs/semconv/registry/attributes/gen-ai/
- MAF Magentic orchestration docs (C# / Python): https://learn.microsoft.com/agent-framework/workflows/orchestrations/magentic
- `MagenticOrchestrator.cs` (event emission, stall/reset/replan logic): https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI.Workflows/Specialized/Magentic/MagenticOrchestrator.cs
- `MagenticProgressLedger.cs` (public ledger fields): https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI.Workflows/MagenticProgressLedger.cs
- `MagenticPlanReviewRequest.cs` (HITL request shape): https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI.Workflows/MagenticPlanReviewRequest.cs
- `MagenticWorkflowBuilder.cs` (limits, default `MaxStalls=3`, `RequirePlanSignoff` default true): https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI.Workflows/MagenticWorkflowBuilder.cs
- MAF 1.0 GA announcement (April 2026): https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/
- Magentic-One paper (original two-ledger design): https://arxiv.org/abs/2411.04468

# Consolidated Spec: Meta-Harness Implementation

## Overview

Implement all concepts from the paper "Meta-Harness: End-to-End Optimization of Model Harnesses" (arXiv:2603.28052) into the Microsoft Agentic Harness project. This is a POC demonstrating automated harness engineering: an outer-loop system that stores full execution traces and uses a coding-agent proposer to search over harness code and improve LLM agent performance over time.

---

## Background

The Meta-Harness paper shows that the choice of agent harness (the code determining what to store, retrieve, and show to the model) can produce a 6x performance gap on the same benchmark. Automated harness engineering requires: (1) rich execution trace storage per run, (2) a proposer agent with filesystem access to read prior traces, and (3) an outer loop that evaluates candidates and stores results.

Key paper findings relevant to this implementation:
- The proposer reads ~82 files per iteration (41% source code, 40% execution traces), not just the latest result
- Execution traces must be grep-friendly (JSONL, flat files) — not DB-only
- The proposer should have causal access: why a run failed, not just that it failed
- Skill text is the primary steering interface for the proposer
- 50–100 example eval tasks is sufficient for discriminative scoring

---

## Scope

All 8 components implemented in a single end-to-end plan, sequentially:

1. Execution Trace Persistence Infrastructure
2. Execution Trace File Structure (grep-friendly)
3. Causal Span Attribution in OpenTelemetry
4. Queryable Agent History (Non-Markovian Memory)
5. SKILL.md Extension (Objectives + TraceFormat sections)
6. Meta-Harness Outer Optimization Loop
7. Harness Candidate Management
8. Config & AppSettings (`MetaHarnessConfig`)

---

## Detailed Requirements

### 1. Execution Trace Persistence Infrastructure

**New interface:** `IExecutionTraceStore` in `Application.AI.Common/Interfaces/`

Modeled after `IArtifactStorageService` from the ApplicationTemplate. Methods:
- `StartRunAsync(runId, metadata)` → creates run directory + `manifest.json`
- `WriteTurnAsync(runId, turnNumber, turnArtifacts)` → writes turn subdirectory artifacts
- `AppendTraceAsync(runId, traceRecord)` → appends single record to `traces.jsonl`
- `WriteScoresAsync(runId, scores)` → writes/updates `scores.json`
- `WriteSummaryAsync(runId, summary)` → writes `summary.md`
- `GetRunDirectoryAsync(runId)` → returns filesystem path for proposer grep access

**New implementation:** `FileSystemExecutionTraceStore` in `Infrastructure.AI/Traces/`

Wired through DI and agent execution pipeline — `AgentExecutionContextFactory` injects it so every agent run automatically records traces.

### 2. Execution Trace File Structure

Root: configurable via `MetaHarnessConfig.TraceDirectoryRoot` (default: `traces/` under app base path).

```
{trace_root}/
  {run_id}/
    manifest.json          # runId, startTime, endTime, harnessVersion, candidateId, taskDescription
    scores.json            # { "pass_rate": 0.72, "token_cost": 14200, "examples": [...per-example results] }
    summary.md             # Human-readable narrative outcome
    traces.jsonl           # Append-only, one JSON object per span/turn
    turns/
      {n}/
        system_prompt.md   # Full system prompt as sent
        tool_calls.jsonl   # One line per tool call with inputs and outputs
        model_response.md  # Raw model response text
        state_snapshot.json # Agent state at turn boundary
```

All files designed for `grep`/`cat` queryability. `traces.jsonl` schema:
```json
{"seq": 1, "ts": "...", "type": "tool_call|tool_result|decision|observation", "run_id": "...", "turn_id": "...", "tool_name": "...", "result_category": "success|partial|error|timeout|blocked", "payload": {...}}
```

### 3. Causal Span Attribution in OpenTelemetry

Extend existing OTel span processor in `Infrastructure.Observability/` to add causal attributes to tool call spans.

Follow OTel GenAI semantic conventions (`execute_tool {name}` span name, `gen_ai.operation.name = "execute_tool"`, `gen_ai.tool.name`).

Add custom causal attributes:
- `tool.input_hash` — SHA256 of serialized tool input (enables cross-run correlation)
- `tool.result_category` — bucketed outcome: `success`, `partial`, `error`, `timeout`, `blocked`
- `gen_ai.harness.candidate_id` — links span to optimization candidate (null for normal runs)
- `gen_ai.harness.iteration` — iteration number in search loop (null for normal runs)

Guard expensive computation with `activity.IsAllDataRequested`.

### 4. Queryable Agent History (Non-Markovian Memory)

**New interface:** `IAgentHistoryStore` in `Application.AI.Common/Interfaces/`

Append-only, immutable event log per session. Methods:
- `AppendAsync(AgentDecisionEvent evt, CancellationToken)` — never update, only append
- `QueryAsync(DecisionLogQuery query, CancellationToken)` → `IAsyncEnumerable<AgentDecisionEvent>`

`AgentDecisionEvent` record: `Sequence`, `Timestamp`, `EventType`, `RunId`, `TurnId`, `ToolName`, `ResultCategory`, `Payload (JsonElement?)`.

`DecisionLogQuery`: filters on `RunId`, `TurnId`, `EventType`, `ToolName`, `Since` (sequence number) — enables checkpoint-based retrieval without loading the full log.

**New implementation:** `JsonlAgentHistoryStore` in `Infrastructure.AI/Memory/` — writes to `decisions.jsonl` in the trace run directory.

**New tool:** `ReadHistoryTool` (keyed as `"read_history"`) — exposes `IAgentHistoryStore.QueryAsync` to the agent as a tool. Registered in `Infrastructure.AI/DependencyInjection.cs` with keyed DI.

### 5. SKILL.md Extension

Add two new first-class section types to the skill definition:

**`## Objectives`** — defines what the agent is optimizing for:
- Success criteria (what does a good outcome look like?)
- Failure patterns to avoid
- Tradeoffs to consider (accuracy vs. token cost)

**`## Trace Format`** — documents the execution trace directory structure for the proposer:
- Directory layout
- File format descriptions
- Key fields to search by

Extend `SkillSection` enum/type to include `Objectives` and `TraceFormat`. Update skill parser (wherever SKILL.md is currently parsed in `Application.AI.Common/`) to extract these sections. Update `ISkillMetadataRegistry` to surface them. Update `skills/research-agent/SKILL.md` as the reference implementation.

### 6. Meta-Harness Outer Optimization Loop

**New interface:** `IHarnessProposer` in `Application.AI.Common/Interfaces/`
```
ProposeAsync(context: HarnessProposerContext, ct) → HarnessProposal
```
`HarnessProposerContext`: current candidate ID, run directory path, prior candidate IDs, iteration number.
`HarnessProposal`: proposed skill file changes, proposed config changes, reasoning text.

**New implementation:** `OrchestratedHarnessProposer` in `Infrastructure.AI/MetaHarness/` — delegates to `RunOrchestratedTaskCommandHandler` with a proposer system prompt and filesystem tool access pointed at the trace directory.

The proposer gets these tools:
- `FileSystemService` (sandboxed to trace directory for read/search)
- `ReadHistoryTool` (queries decision log)
- Shell tool (full shell commands, working directory restricted to trace directory)
- MCP trace resources (trace filesystem exposed as MCP resources — see §MCP below)

**New interface:** `IEvaluationService` in `Application.AI.Common/Interfaces/`
```
EvaluateAsync(candidate: HarnessCandidate, evalTasks: IReadOnlyList<EvalTask>, ct) → EvaluationResult
```
Runs the agent against each eval task, collects pass/fail + token cost, returns aggregate `EvaluationResult`.

**New command + handler:**
- `RunHarnessOptimizationCommand` in `Application.Core/CQRS/MetaHarness/`
- `RunHarnessOptimizationCommandHandler` — outer loop:

```
for i in 1..config.MaxIterations:
  1. Load current best candidate
  2. Invoke proposer → HarnessProposal
  3. Create new HarnessCandidate from proposal (snapshot skill files + config)
  4. Evaluate candidate against eval tasks → EvaluationResult
  5. Store traces + scores for this candidate
  6. If eval failed (runtime error): log failure, store error trace, continue
  7. If score improved: update best candidate
  8. Store candidate in IHarnessCandidateRepository
  9. Prompt user if manual review needed (configurable)
```

**Entry point:** New `optimize` command in `Presentation.ConsoleUI` that invokes `RunHarnessOptimizationCommand`.

**MCP exposure:** Trace filesystem exposed as MCP resources in `Infrastructure.AI.MCP/` — trace directories as listable resources, individual trace files as readable resources.

### 7. Harness Candidate Management

**New domain model:** `HarnessCandidate` in `Domain.Common/MetaHarness/`
- `CandidateId` (Guid)
- `ParentCandidateId` (Guid?) — null for seed/baseline
- `CreatedAt` (DateTimeOffset)
- `Iteration` (int)
- `SkillFileSnapshots` (IReadOnlyDictionary<string, string>) — skill path → content
- `SystemPromptSnapshot` (string)
- `ConfigSnapshot` (IReadOnlyDictionary<string, string>) — config key → value
- `BestScore` (double?) — set after evaluation
- `TokenCost` (long?) — cumulative tokens used in evaluation
- `Status` — `Proposed`, `Evaluated`, `Failed`, `Promoted`

**New interface:** `IHarnessCandidateRepository` in `Application.AI.Common/Interfaces/`
- `SaveAsync(candidate, ct)`
- `GetAsync(candidateId, ct)` → `HarnessCandidate?`
- `GetLineageAsync(candidateId, ct)` → `IReadOnlyList<HarnessCandidate>` (ancestor chain)
- `GetBestAsync(runId, ct)` → `HarnessCandidate?`
- `ListAsync(runId, ct)` → `IReadOnlyList<HarnessCandidate>` (all candidates for a run)

**New implementation:** `FileSystemHarnessCandidateRepository` in `Infrastructure.AI/MetaHarness/` — stores each candidate as a directory under `{trace_root}/{run_id}/candidates/{candidate_id}/` with a `candidate.json` snapshot.

### 8. Config & AppSettings

**New config class:** `MetaHarnessConfig` in `Domain.Common/Config/MetaHarness/`

```csharp
public sealed class MetaHarnessConfig
{
    public string TraceDirectoryRoot { get; init; } = "traces";
    public int MaxIterations { get; init; } = 10;
    public int SearchSetSize { get; init; } = 50;         // number of eval tasks per candidate
    public double ScoreImprovementThreshold { get; init; } = 0.01;  // min improvement to consider better
    public bool AutoPromoteOnImprovement { get; init; } = false;     // false = always write to disk for user review
    public string EvalTasksPath { get; init; } = "eval-tasks";       // relative path to eval task files
    public string SeedCandidatePath { get; init; } = "";             // path to initial harness to start from
}
```

Nested under `AppConfig` as `AppConfig.MetaHarness`. Wire via `IOptionsMonitor<MetaHarnessConfig>`. Add `appsettings.json` section with documented defaults.

---

## Architecture Constraints

- Clean Architecture: Domain → Application → Infrastructure → Presentation. No layer violations.
- New interfaces: `Application.AI.Common/Interfaces/` (grouped by feature: `MetaHarness/`, `Memory/`, `Traces/`)
- New implementations: `Infrastructure.AI/MetaHarness/`, `Infrastructure.AI/Traces/`, `Infrastructure.AI/Memory/`
- New CQRS: `Application.Core/CQRS/MetaHarness/`
- New domain models: `Domain.Common/MetaHarness/`
- Keyed DI for all new tools registered in `Infrastructure.AI/DependencyInjection.cs`
- `Result<T>` pattern for all failure cases in command handlers
- FluentValidation on `RunHarnessOptimizationCommand`
- Functions <50 lines, files <400 lines
- 80% test coverage on new code: unit tests (mock proposer + evaluator) + integration tests (temp dir + scripted proposer)

---

## Testing Requirements

**Unit tests** (mock `IHarnessProposer` + `IEvaluationService`):
- Loop iterates correct number of times
- Failed evaluation stores error trace and continues
- Score improvement updates best candidate
- Candidate lineage is maintained correctly

**Integration tests** (temp directory, scripted/canned proposer):
- `FileSystemExecutionTraceStore` writes and reads back correct artifacts
- `JsonlAgentHistoryStore` appends and queries events correctly
- `FileSystemHarnessCandidateRepository` stores, retrieves, and lists candidates
- Full loop with scripted proposer produces expected candidate directory structure

**Existing tests** should continue to pass. The new trace store hooks must not break existing agent execution.

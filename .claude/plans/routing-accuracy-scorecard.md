# Plan: Routing-Accuracy Scorecard

**Status:** Greenlit (full scope) — pending implementation go-ahead
**Origin:** Assessment of "Router vs Orchestrator Agents" article — see memory `project_routing_accuracy_scorecard`
**Scope:** Cover both classifying routers (RAG query-type + agent task-complexity). Advisory, runs on the existing scheduled eval job. No per-PR hard gate.

---

## 1. Goal

Measure whether our routers classify correctly, as a repeatable scorecard. Feed each router a set of labeled example inputs (where we already know the right bucket), compare its decision against the label, and report % correct. Run it whenever a prompt or model changes so a routing regression surfaces before users feel it.

The harness already has every orchestration building block the article describes **except** an automated routing-accuracy measurement. This closes that gap and inherits to every consumer who clones the template.

## 2. What we are NOT building (explicit scope guards)

- **No new eval engine.** Reuse `Infrastructure.AI.Evaluation` wholesale — runner, reporters (JSON/JUnit/console), YAML dataset loader, CI job (`eval-suite.yml`), median-over-repeats aggregation.
- **No changes to the routers.** We wrap their existing interfaces; production routing code is untouched.
- **No semantic-vector router.** Documented as a known option, not built (YAGNI).
- **No per-PR hard gate.** Router classification is a live LLM call and mildly non-deterministic; gating every PR would be flaky. Advisory on the daily scheduled run, matching the existing eval-suite pattern.
- **No ModelRouter tier-mapping eval.** Tier selection downstream of complexity is deterministic config; testing the *complexity classification* covers the judgment call. (Covered indirectly.)

## 3. Design decision: decorator + keyed probe (chosen)

### The seam
`EvalRunner` (`Infrastructure.AI.Evaluation/Runners/EvalRunner.cs`) is constructor-injected with **one** `IAgentInvoker`. Per case it calls `InvokeAsync(case, ...) -> AgentInvocationResult`, then each `IEvalMetric` scores `(case, result, spec)`. `AgentInvocationResult.Output` is plain text. Cases already carry a free-form `InvocationOverrides` string dict and a `MetricSpecs` list.

### Chosen approach
1. **`IRouterEvalProbe`** (new, Application) — a tiny adapter over a router. `Task<RouterDecision> ClassifyAsync(string input, IReadOnlyDictionary<string,string> parameters, CancellationToken)`. Registered as **keyed** services (`"query_type"`, `"task_complexity"`) — same keyed-DI pattern the harness uses for tools/step-executors, so consumers can add their own router probes without editing our code.
2. **`RouterEvalInvoker : IAgentInvoker`** (new, Infrastructure.AI.Evaluation) — a **decorator** over the existing `HarnessAgentInvoker`. Reads `case.InvocationOverrides["target"]`:
   - absent or `"agent"` → delegate to inner harness invoker (existing behavior, untouched).
   - `"router:<key>"` → resolve the keyed `IRouterEvalProbe`, call it, pack `decision.Label` into `AgentInvocationResult.Output`.
3. **`RoutingAccuracyMetric : IEvalMetric`** (new, Infrastructure.AI.Evaluation, key `"routing_accuracy"`) — compares `output.Output` (the predicted label) to `case.ExpectedOutput` (the gold label), enum-normalized (case-insensitive, underscores/Pascal tolerant). Score 1.0/0.0; `Warn` when no expected label. ~40 lines, structurally a sibling of `ExactMatchMetric`.

### Why this and not the alternative
**Rejected — add a `Target` field to `EvalCase` and branch inside `EvalRunner`.** That mutates the Domain model and the core runner for a concern they don't need to know about. The decorator keeps the Domain model and runner frozen, uses the `InvocationOverrides` bag that already exists, and makes "what am I invoking?" a pluggable, consumer-extensible concern. Better long-term separation for the same effort.

### Why probes instead of referencing routers directly
Keeps `Infrastructure.AI.Evaluation` from taking hard references on `Infrastructure.AI.RAG` and `Infrastructure.AI` routing internals. The probe interface lives in Application; each Infrastructure project implements its own probe and self-registers. Open for extension, closed for modification.

## 4. File-by-file changes

### Domain
- *(none)* — `EvalCase`/`MetricSpec` unchanged.

### Application (`Application.AI.Common`)
- **NEW** `Evaluation/Interfaces/IRouterEvalProbe.cs` — keyed probe interface. Full XML docs.
- **NEW** `Evaluation/Models/RouterDecision.cs` — `record { string Label; double Confidence; string? Reasoning; string? SecondaryLabel }`. (`SecondaryLabel` carries the RAG retrieval-strategy alongside the query-type for richer future reporting; metric uses `Label` only for now.)

### Infrastructure.AI.RAG
- **NEW** `Evaluation/QueryTypeRouterProbe.cs` — implements `IRouterEvalProbe`, wraps the existing `IQueryClassifier` (`LlmQueryClassifier`). Maps `QueryClassification.Type` → `RouterDecision.Label`, `.Strategy` → `SecondaryLabel`. No change to the classifier.
- DI: register keyed `"query_type"`.

### Infrastructure.AI
- **NEW** `Routing/Evaluation/TaskComplexityRouterProbe.cs` — implements `IRouterEvalProbe`, wraps existing `ITaskComplexityClassifier`. Adapts the input string into a minimal `AgentTurnContext` (turn 1, message = input, tool count from optional `parameters["tool_count"]`). Maps `TaskComplexityAssessment.Complexity` → `Label`. No change to the classifier.
- DI: register keyed `"task_complexity"`.

### Infrastructure.AI.Evaluation
- **NEW** `Invokers/RouterEvalInvoker.cs` — the decorator (above).
- **NEW** `Metrics/RoutingAccuracyMetric.cs` — the metric (above).
- **EDIT** `DependencyInjection.cs` — register `RoutingAccuracyMetric` via the existing `AddMetric` helper (key `routing_accuracy`); decorate the registered `IAgentInvoker` so the runner resolves `RouterEvalInvoker` wrapping `HarnessAgentInvoker`. Probes are registered by their owning projects' DI, pulled in here via DI.

### Dataset
- **NEW** `eval-datasets/seed/routing-accuracy.yaml` — ~35 labeled cases (spec in §5). Auto-discovered by the existing CI glob `eval-datasets/seed/*.yaml`.

### Tests (xUnit, mirror existing eval test projects)
- `RoutingAccuracyMetricTests` — exact label, normalized label, mismatch, no-expected→Warn.
- `RouterEvalInvokerTests` — agent passthrough (no target), router target resolves probe + packs label, unknown target key → graceful failure result.
- `QueryTypeRouterProbeTests` / `TaskComplexityRouterProbeTests` — mock the underlying classifier, assert mapping.

### CI / docs
- **No workflow change** — `eval-suite.yml` already globs the seed folder and runs with `--deterministic --repeats 3`, advisory.
- **EDIT** eval README / docs — document the `target: router:<key>` case convention, the new metric, and the semantic-vector router as a known-but-unbuilt option.

## 5. Dataset spec (`routing-accuracy.yaml`)

~35 cases. Each: `input` (the query), `expected_output` (gold enum label), `invocation_overrides: { target: "router:<key>" }`, `metric_specs: [{ metric_key: routing_accuracy }]`, `tags: [routing, <router>]`.

- **Query-type router** (`router:query_type`) — ~5 each across the 5 buckets: `SimpleLookup`, `MultiHop`, `GlobalThematic`, `Comparative`, `Adversarial`. (~25 cases)
- **Task-complexity router** (`router:task_complexity`) — ~2-3 each across the 4 levels: `Trivial`, `Simple`, `Moderate`, `Complex`. (~10 cases)

Starter set is hand-authored and deliberately unambiguous. Curating toward the article's ~200, including the genuinely-borderline cases, is follow-on work tracked separately — the first version proves the machinery and gives a real baseline.

## 6. Open decisions / flags for review

1. **`AgentInvocationResult` has no metadata bag.** We pack only the label into `Output`; confidence/reasoning from `RouterDecision` are not surfaced to reports yet. Fine for accuracy scoring. Adding a metadata dict to surface confidence is a deliberate future enhancement, not this PR (YAGNI).
2. **Complexity probe synthesizes an `AgentTurnContext`** from a bare string (turn 1, derived tool count). That's a faithful single-turn approximation; multi-turn complexity signals aren't exercised. Acceptable for a classification scorecard; noted as a limitation.
3. **Live LLM cost.** Economy-tier calls, on the scheduled run only, `--deterministic`. Consistent with existing eval-suite spend posture.

## 7. Effort / risk

- **~10-12 files**, most small; the metric ≈ `ExactMatchMetric`, the probes are thin mappers.
- **Risk: low.** Additive only. Zero change to production routers, the runner, the Domain model, or existing datasets. The decorator's default branch preserves all current behavior.
- **Real work is the dataset** — labeling, not plumbing. Exactly as the article argues.

## 8. Verification

`dotnet build src/AgenticHarness.slnx && dotnet test src/AgenticHarness.slnx`, then a local `EvalRunner` run over `routing-accuracy.yaml` (`--deterministic`) to confirm the scorecard produces a baseline number. Then `/code-review` + `/simplify` per project review cadence before pushing.

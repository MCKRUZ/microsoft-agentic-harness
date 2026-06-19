# Plan: Judge-Panel (Jury) Evaluator

**Status:** IMPLEMENTED + fully tested (2026-06-19), pending review/commit. Full solution builds clean; 205 eval + 1065 app-common + 790 domain C# tests pass (incl. the 9 original DefaultLlmJudge regression tests + empty-panel-equals-single-judge proof); 6 dashboard tests pass. Not yet committed.
**Origin:** Assessment of "Multi-Model Code Review" article (Anna Jey). Single genuinely-new idea worth stealing: the *consensus aggregator* — run an artifact past several independent reviewers and weight the result by agreement. Everything else the article describes the harness already has (lenses = our metric packs, structured findings, swappable models, injection-hardened prompt contracts, advisory-not-blocking). See memory `project_judge_panel_evaluator`.
**Scope:** Upgrade the eval framework's single LLM judge into an optional *panel of judges* (a jury). Reduces score noise (median over a panel) and surfaces disagreement (consensus / split / conflict) as first-class signal **on a dedicated dashboard column**. Additive; default config reproduces today's single-judge behavior byte-for-byte.

**Locked decisions (Matt, this session):**
1. **Off by default** — ship empty panel (single-judge, zero cost change); multi-model/multi-persona documented as examples.
2. **Median-only v1** — no per-panelist Quorum/Unanimous voting (avoids threshold duplication).
3. **Dedicated dashboard column** — consensus is surfaced as a first-class `MetricScore` field and rendered as its own column in the Evals run-detail page (NOT deferred, NOT folded only into Reasoning text).

---

## 1. Goal

Replace "one LLM judge scores each case" with "an optional panel scores each case, and we report both the robust aggregate and how much the panel disagreed." A single judge has blind spots and the occasional confident-but-wrong call; a jury's median absorbs the outlier and the spread tells a human where to look. This is the known "LLM-as-jury beats LLM-as-judge" result, implemented against our existing judge seam so **every** judge-backed metric inherits it at once.

The harness already has every other piece the article describes. This closes the one gap and inherits to every consumer who clones the template.

## 2. What we are NOT building (explicit scope guards)

- **No new judge-call mechanics.** The nonce-envelope injection defense, HtmlEncode, retry, soft-fail, and cost accounting in `DefaultLlmJudge` are the crown jewels — they get **extracted and reused**, never duplicated.
- **No literal reuse of `IApprovalStrategy` (Quorum/AllOf/AnyOf).** That code is the *conceptual* template (N-of-M, fail-closed on misconfig) but it is typed for human `ApproverDecision`/`EscalationRequest` and resolves a boolean. A jury aggregates continuous [0,1] scores. We build a sibling aggregator in the evaluation domain rather than bending the escalation types — see §3.
- **No per-panelist pass/fail "strategy" (Unanimous/Majority) in v1.** That requires the jury to know each metric's threshold, duplicating threshold ownership that today lives in the metric. v1 aggregates by **median score** (the variance-reduction win) and reports disagreement separately. Quorum-style per-panelist voting is documented as a known option, not built (YAGNI + avoids threshold duplication).
- **No same-model-repeat as the headline use.** Running one model N times only reduces sampling noise, which the suite already gets from `--repeats 3 → median`. The jury's distinctive value is **diverse panelists** (different models and/or different rubric personas). Same-model-repeat still works but isn't the pitch.
- **No router-model routing for judges.** Preserved: the judge deliberately bypasses `IModelRouter` for reproducibility. A panel of *fixed* distinct models is still reproducible; we keep that invariant.
- **No governance-gate consumer.** The jury lives in the eval framework. Governance gates can consume the richer signal later; not this PR.
- **No new JSON reporter code.** Consensus surfaces as first-class fields on `MetricScore`; `JsonEvalReporter` serializes the whole report graph reflectively, so the new fields flow to the JSON artifact automatically (snake_case, enum via the existing `JsonStringEnumConverter`). The dashboard reads that artifact. Zero reporter changes.

## 3. Design decision: decorate `ILlmJudge` + extract a shared call-core (chosen)

### The seam
`ILlmJudge.JudgeAsync(LlmJudgeRequest) → LlmJudgeResult` is the single dependency of **all six** judge-backed metrics: the rubric `LlmJudgeMetric` plus the RAG pack (faithfulness, context precision, context recall, answer relevance, answer correctness). Swapping the `ILlmJudge` registration upgrades all six with no metric changes — the exact analog of how `RouterEvalInvoker` decorated `IAgentInvoker` for the routing scorecard.

### Chosen approach
1. **Extract the call-core.** Pull the injection-defense + invoke + parse + retry + cost mechanics out of `DefaultLlmJudge` into a shared internal executor that accepts an explicit `IChatClient` and an optional **trusted** system-prompt augmentation (the panelist's persona/lens, sourced from config — never from a case). `DefaultLlmJudge` keeps its exact current behavior by resolving the default judge client and calling this core once. Guarded by the existing `DefaultLlmJudgeTests` (proves zero behavior drift).
2. **`JuryLlmJudge : ILlmJudge`** (new, decorator over the core) — for each configured panelist: resolve that panelist's client, run the shared core **in parallel** (`Task.WhenAll`), then aggregate. **Empty panel → resolve the default judge and run the core once → identical to today.** This makes config the only switch; no conditional DI.
3. **`JuryAggregator`** (new, pure/stateless — sibling of the skill-training `PatchAggregator`/`GateEvaluator`) — takes the panelist verdicts and computes: aggregate **score = median** (configurable Median/Mean/Min), **spread = max − min**, and a **consensus bucket** (Consensus / Split / Conflict by spread thresholds). Pure, no I/O, exhaustively unit-testable.
4. **Resilience.** A panelist that fails or returns malformed JSON is excluded; the rest still aggregate. Only one survivor → degrade to single (noted in reasoning). All fail → preserve the existing `Malformed`/`InvocationFailed` soft-fail contract. Fail-closed, consistent with governance.

### Why this and not the alternatives
- **Rejected — a new `jury_judge` metric alongside the others.** That makes the jury opt-in per case and leaves the RAG pack on a soloist. Decorating `ILlmJudge` upgrades everything and keeps the metrics ignorant of panel mechanics. Better separation for the same effort.
- **Rejected — collapse the panel and only return the median score.** Throws away the disagreement signal, which is half the article's value. We surface the panel explicitly (additive field on the result + folded into Reasoning/RawOutput).
- **Rejected — reuse `IApprovalStrategy`.** Wrong type shape (discrete human votes vs continuous scores) and would drag escalation domain types into the eval framework. Sibling aggregator is cleaner and honest about the difference.

## 4. File-by-file changes

### Domain (`Domain.AI/Evaluation`)
- **NEW** `ConsensusBucket.cs` — enum `Consensus` / `Split` / `Conflict`. **Must live in Domain** (not Application) because `MetricScore` references it and Domain depends on nothing. (Layering correction from the first draft.)
- **EDIT** `MetricScore.cs` — add `ConsensusBucket? Consensus { get; init; }` and `double? Spread { get; init; }` (both nullable, additive; `null` for every non-jury metric → the dashboard column shows "—"). These are the Domain-resident *summary* the dashboard column binds to; the full per-panelist detail stays Application-side (below) and rides in `RawOutput` for forensics.

### Application (`Application.AI.Common/Evaluation`)
- **NEW** `Models/JuryOptions.cs` — `Panelists` (list), `ScoreAggregation` enum (Median default / Mean / Min), `ConsensusMaxSpread` (default 0.2), `ConflictMinSpread` (default 0.5). Empty `Panelists` ⇒ single-judge behavior. Full XML docs.
- **NEW** `Models/JuryPanelistSpec.cs` — `Name` (label), `ClientType?`, `Deployment?` (both default to `JudgeOptions`), `PersonaPrompt?` (the trusted "lens" appended to the judge system core).
- **NEW** `Models/JuryPanelResult.cs` — `IReadOnlyList<PanelistVerdict> Verdicts`, `Domain.AI.Evaluation.ConsensusBucket Bucket`, `double Spread`, `int Responded`/`int Excluded`. (Application → Domain reference is allowed.)
- **NEW** `Models/PanelistVerdict.cs` — `Name`, `Score`, `Reasoning`, `LlmJudgeOutcome Outcome`, `CostUsd`.
- **NEW** `Evaluation/JuryAggregator.cs` — pure aggregation (median/mean/min, spread, bucket). No deps. (Mirrors the stateless pure-component convention from skill-training.)
- **EDIT** `Models/LlmJudgeResult.cs` — add `JuryPanelResult? Panel { get; init; }` (additive, nullable; `null` for single-judge — back-compat, callers untouched).
- **EDIT** `Interfaces/IJudgeChatClientProvider.cs` — add overload `GetJudgeAsync(AIAgentFrameworkClientType clientType, string deployment, CancellationToken)` to resolve a specific panelist model. Additive; existing param-less method (default judge) unchanged.

### Infrastructure.AI.Evaluation (`Judges/`)
- **REFACTOR** `DefaultLlmJudge.cs` — extract the envelope/invoke/parse/retry/cost mechanics into a shared internal core (e.g. `JudgeCallExecutor`) that takes an `IChatClient` + optional trusted system augmentation. `DefaultLlmJudge` becomes: resolve default client → core (behavior identical).
- **NEW** `JuryLlmJudge.cs` — the decorator (§3.2). Resolves each panelist via the provider overload, runs the core in parallel, calls `JuryAggregator`, builds `JuryPanelResult`, folds a one-line consensus summary into `Reasoning` and the structured panel into `RawOutput`, sums `CostUsd`/tokens across panelists.
- **EDIT** `DefaultJudgeChatClientProvider.cs` — implement the new explicit-`(clientType, deployment)` overload. Trivial: the internal cache is **already** keyed by `(clientType, deployment)`; the param-less method just reads `JudgeOptions` then resolves that key. Extract the shared resolve-by-key path.
- **EDIT** `DependencyInjection.cs` — `AddOptions<JuryOptions>()`; swap `services.AddSingleton<ILlmJudge, DefaultLlmJudge>()` → register `JuryLlmJudge` as the public `ILlmJudge` (keep `DefaultLlmJudge`/core registered as the single-panelist executor it delegates to for the empty/default case). Default (no panelists configured) = today's behavior.

### Infrastructure.AI.Evaluation (`Metrics/`) — surface consensus on `MetricScore`
There are **exactly two** sites that build a judge-backed `MetricScore` (verified). Both copy `result.Panel` → the new `MetricScore.Consensus`/`Spread` when the panel is present (`null`-safe; single-judge leaves them null):
- **EDIT** `Metrics/LlmJudgeMetric.cs` — in the `Parsed` branch only.
- **EDIT** `Metrics/Rag/RagJudgeMetricBase.cs` — in the `Parsed` branch only (covers all 5 RAG metrics).

### Config
- **DOC** Example `JuryOptions` block (commented, empty by default) in the eval host `appsettings.json`, showing a 3-model panel and a 3-persona panel. No behavior change until populated.

### Presentation (`Presentation.Dashboard`) — the dedicated column
- **EDIT** `src/api/evals.ts` — add `consensus?: 'consensus' | 'split' | 'conflict'` and `spread?: number` to the TS `MetricScore` type (mirrors the new Domain fields; snake_case from the JSON artifact).
- **EDIT** `src/routes/Evals/EvalRunDetailPage.tsx` — add a dedicated **"Consensus"** column (`<th>` + `<td>`) to the case table. Per judge-metric, render a color-coded chip — `Consensus` → `otel-positive` (green), `Split` → `otel-warning` (amber), `Conflict` → `otel-negative` (red) — with the spread value; non-judge metrics render "—". Reuses the existing `verdictColor` palette pattern. Scores are already stacked per-metric in a cell, so the consensus cell lists one chip per judge-metric in the same order.
- **TEST** `EvalRunDetailPage.test.tsx` (sibling exists) — assert the column renders the right chip/color for each bucket and "—" when absent.

### Tests (xUnit, mirror existing eval test projects)
- `JuryAggregatorTests` (pure) — median odd/even counts; spread→bucket boundaries (Consensus/Split/Conflict); single-panelist; all-excluded.
- `JuryLlmJudgeTests` — 3 mocks agree → `Consensus` + median; one outlier → `Split`/`Conflict`, median absorbs it; one panelist malformed → excluded, rest aggregate; all malformed → `Malformed` (contract preserved); **empty panel → byte-identical to single judge** (delegation proof).
- `DefaultJudgeChatClientProviderTests` — new overload resolves by explicit `(type, deployment)` and shares the existing single-flight cache.
- **Regression:** existing `DefaultLlmJudgeTests` stay green after the core extraction (no behavior drift).

## 5. Behavior / cost notes (call out before build)

1. **Cost multiplier.** A panel of N runs N judge calls per case → N× judge spend. Mitigations: panel runs in parallel (latency stays ~1×), cost already accumulates per-call (the jury sums it, so reports stay honest), and the panel is **opt-in via config** — zero extra cost until a consumer populates `Panelists`. Recommend enabling on the scheduled suite, not per-PR.
2. **Two diversity modes, same N× cost.** Different *models* (the article's pitch — needs ≥2 providers configured) **or** different *rubric personas on one model* (works with a single provider). For a consumer with one provider, multi-persona is the recommended starter.
3. **CI degradation is graceful.** A CI runner with one provider and a multi-model panel: missing panelists are excluded, the panel degrades toward single — no failure, just less diversity. Documented.
4. **Reproducibility preserved.** Fixed panel of distinct models = reproducible across runs (still bypasses `IModelRouter`). Same-model-repeat introduces sampling noise the median absorbs; not the headline use.

## 6. Decisions (all locked — no open questions)

1. **Off by default** ✓ — ship empty panel; multi-model and multi-persona panels documented as commented config examples. Zero cost change until a consumer opts in.
2. **Median-only v1** ✓ — Quorum-style per-panelist voting (Unanimous/Majority) is out of scope for the threshold-ownership reason in §2; documented as a known future option.
3. **Dedicated dashboard column** ✓ — consensus is a first-class `MetricScore` field (Domain) rendered as its own color-coded column in the Evals run-detail page; see §4 Presentation.

Remaining judgment lives in *configuration*, not code: the choice of panelists (which models / which personas) and the disagreement thresholds (`ConsensusMaxSpread` / `ConflictMinSpread`). Sensible defaults ship; tuning is consumer work, same lesson as the routing scorecard.

## 7. Effort / risk

- **~18–20 files** — Domain enum + `MetricScore` edit; five small Application models; one pure aggregator; one decorator; the `DefaultLlmJudge` core extraction; a provider overload; two metric Parsed-branch edits; DI + config; dashboard type + page + test; C# tests. Most are small.
- **Risk: low-moderate.** Additive **except** three guarded touch-points: (1) the `DefaultLlmJudge` core extraction — guarded by existing `DefaultLlmJudgeTests`; (2) the `ILlmJudge` registration swap — guarded by the empty-panel-equals-single-judge test; (3) the two `MetricScore` construction edits — additive nullable fields, existing metric tests stay green. Default config = today's behavior exactly.
- **Cross-layer span:** Domain (enum + result field), Application (models + aggregator), Infrastructure (judge + metrics + DI), Presentation (dashboard column). All four layers, but each touch is thin. Follows the Clean Architecture new-feature checklist top-to-bottom.
- **Real work is the design of the persona/model panel and the disagreement thresholds**, not plumbing — same lesson as the routing scorecard: the judgment lives in the configuration, not the code.

## 8. Verification

`dotnet build src/AgenticHarness.slnx && dotnet test src/AgenticHarness.slnx`, then a local `EvalRunner` run over an existing judge-backed dataset with (a) empty panel → confirm scores match the pre-change baseline, and (b) a 3-persona panel → confirm the panel result, spread, and bucket appear in the JSON report. Then `/code-review` + `/simplify` per project review cadence before pushing. Run `gitnexus_impact` on `DefaultLlmJudge` and `ILlmJudge` before editing (shared seam — expect judge-backed metrics in the blast radius) and `gitnexus_detect_changes()` before commit.

# Self-Improving Work Memory (Perplexity "Brain" parity)

## Origin
Research project: Perplexity **Brain** (launched 2026-06-18) — a *self-improving work memory*
for agents. Distinct from user-preference memory: it remembers **what the agent did** (what
worked, what failed, what the user corrected), runs an **overnight synthesis** pass that distills
raw session logs into reusable lessons stored in an auto-loaded "LLM wiki" / context graph, so
each new task **starts with better context**. Claimed: +25% correctness, +16% recall, −13% cost
on previously-seen tasks. (Numbers are Perplexity's own research-preview measurement — directional,
not independently verified.)

## Gap analysis vs. this harness
The harness already has ~80% of the primitives and **exceeds** Brain on provenance, right-to-erasure,
and multi-tenant isolation. Existing pieces:
- Knowledge graph (Neo4j/Kuzu/Postgres) + entity extraction
- `Learnings` subsystem (`LearningEntry`, `ILearningsStore`, `GraphLearningsStore`, EMA feedback,
  decay/pruning) — but stores **isolated facts**, not **work trajectories**
- Cross-session memory (`IKnowledgeMemory` Remember/Recall/Forget/Improve) — **user/domain facts**
- Feedback (`LlmFeedbackDetector`, `GraphFeedbackStore`), provenance (`DefaultProvenanceStamper`)
- `KnowledgeMemoryContextProvider` injects recalled facts each turn
- SkillOpt 6-stage loop (self-improvement) — but trains on **synthetic rollouts**, not real sessions

### The three real gaps
1. **No work-episode capture** — nothing records what the agent *did* on a task (outcome, cost,
   correction). Memory stores facts, not trajectories. *Foundational.*
2. **No synthesis/consolidation job** — the background service only prunes/decays; nothing distills
   raw sessions into higher-level lessons.
3. **Self-improvement runs on synthetic data** — SkillOpt never sees real production sessions; the
   loop "real work → lessons → better next task" is open.

## Plan — three PRs, dependency order

### PR1 — Work-episode capture (foundation) — `feat/work-memory-pr1-episode-capture`
Record each agent turn as a cheap, structural **work episode** (no LLM in the hot path).
- **Domain** (`Domain.AI/WorkMemory/`): `WorkEpisode` record, `EpisodeOutcome` enum.
- **Application** (`Application.AI.Common/Interfaces/WorkMemory/`): `IWorkEpisodeStore`
  (Save/Get/Search), `WorkEpisodeSearchCriteria`.
- **Application**: `WorkEpisodeCaptureBehavior` — mirrors `KnowledgeExtractionBehavior`
  (fire-and-forget, fresh DI scope, ambient-scope re-establishment so tenant isolation resolves).
  Builds an episode from `IAgentTurnRequest`/`IAgentTurnResult` and persists it.
- **Config**: `WorkMemoryConfig` under `AppConfig:AI:WorkMemory` (Enabled, StoreProvider,
  ResponseSummaryMaxChars). Master toggle **off-by-default** until PR2/PR3 consume it.
- **Infrastructure** (`Infrastructure.AI.KnowledgeGraph/WorkMemory/`): `GraphWorkEpisodeStore`
  (keyed `"graph"`, mirrors `GraphLearningsStore`, indexed by conversation), `InMemoryWorkEpisodeStore`
  (keyed `"in_memory"`). Tenant/owner isolation inherited from the decorated `IKnowledgeGraphStore`.
- **Tests**: domain defaults, behavior gating + fire-and-forget, in-memory store, graph round-trip.
- **Deliberately NOT in PR1** (YAGNI): per-tool step granularity (no reliable source at the turn
  seam — needs a deeper function-invocation hook; revisit only if a consumer needs it).

### PR2 — Overnight synthesis (consolidation) — depends on PR1
Scheduled `BackgroundService` (mirrors `LearningsPruningBackgroundService`) that reads recent
`WorkEpisode`s and distills them into `LearningEntry`s via an LLM synthesizer:
- Detect corrections / dead-ends / repeated-success patterns across episodes.
- Write lessons through the **existing** `Learnings` store with `LearningSourceType.AgentSelfImprovement`.
- Reuse the RAPTOR hierarchical-summarization pattern for cross-episode clustering.
- **Security prerequisite:** synthesized lessons are model-generated from session content → the
  [[guarding-ai-memory]] write-path injection scan + trust marker must gate this write. Fold that
  gate into PR2 (it becomes a hard requirement, not a nice-to-have, the moment we ingest sessions).

### PR3 — Task-similarity recall at task start — depends on PR1+PR2
Extend `KnowledgeMemoryContextProvider` (or a sibling provider) to recall **"this task resembles
past task Y; here's what worked"** from synthesized lessons + episode outcomes, injected at turn
start. Closes the loop; enables the correctness/recall/cost measurement.

## Deferred cleanup (surfaced in PR1 review, intentionally not done)
- **Shared graph-keyed-store base:** `GraphWorkEpisodeStore` duplicates ~80% of
  `GraphLearningsStore` (const node/index keys, `ToNodeId`/`Extract*`, `CollectIndexNeighborsAsync`,
  full-scan fallback). Only two instances exist (rule of three not met) and abstracting touches
  working code. Revisit when a third graph-keyed store appears — then extract a base in its own PR.
- **Fire-and-forget backpressure:** both `WorkEpisodeCaptureBehavior` and the reference
  `KnowledgeExtractionBehavior` use unbounded `Task.Run` per turn. If this becomes a load concern,
  bound it cross-cuttingly for both behaviors, not just this one.

## Status
- PR1: complete — build green, 27 new tests pass, `/code-review` + `/simplify` run
  (validator + surrogate-pair fixes applied; duplication/backpressure deferred above).
- PR2, PR3: not started.

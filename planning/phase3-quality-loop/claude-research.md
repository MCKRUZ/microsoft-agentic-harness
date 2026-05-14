# Phase 3 Research — Drift Detection + Learnings Log

## Part 1: Codebase Research

### 1. Evaluation & Scoring Infrastructure

**`IEvaluationService`** (`Application.AI.Common/Interfaces/MetaHarness`)
- Single method: `EvaluateAsync(HarnessCandidate, IReadOnlyList<EvalTask>, CancellationToken)`
- Returns `EvaluationResult` with `CandidateId`, `PassRate` (0.0-1.0), `TotalTokenCost`, `PerExampleResults`

**`HarnessScores`** (`Domain.Common/MetaHarness`) — immutable record
- `PassRate` (double 0.0-1.0), `TotalTokenCost` (long), `PerExampleResults` (ExampleResult[]), `ScoredAt` (DateTimeOffset)
- `ExampleResult`: `TaskId`, `Passed` (bool), `TokenCost` (long)

**`IExecutionTraceStore`** (`Application.AI.Common/Interfaces/Traces`)
- `StartRunAsync(TraceScope, RunMetadata)` — creates run dir, writes manifest.json, returns `ITraceWriter`
- `GetRunDirectoryAsync(TraceScope)` — locating trace directories
- **Implementation:** `FileSystemExecutionTraceStore` (Infrastructure.AI/Traces)
  - Per-run directory: `manifest.json`, `turns/{turnNumber}/system_prompt.md`, `tool_calls.jsonl`, `model_response.md`, `state_snapshot.json`
  - Tool results: `turns/{turnNumber}/tool_results/{callId}.json` (truncated > MaxFullPayloadKB)
  - Atomic writes via temp file + rename; secret redaction via `ISecretRedactor`

**MetaHarness Integration:** Optimization loop in `RunHarnessOptimizationCommandHandler` invokes `IEvaluationService.EvaluateAsync()` per candidate; traces written via `ITraceWriter`.

### 2. Feedback & Learning Infrastructure

**`IFeedbackStore`** (`Application.AI.Common/Interfaces/KnowledgeGraph`)
- `GetNodeWeightAsync(nodeId)` -> `NodeFeedbackWeight`
- `GetEdgeWeightAsync(edgeId)` -> `EdgeFeedbackWeight`
- `ApplyNodeFeedbackAsync(nodeId, feedbackScore: 1.0-5.0, alpha: 0.0-1.0)`
- Batch: `GetNodeWeightsBatchAsync()`, `DeleteWeightsByNodeIdsAsync()`
- Default weight: 1.0 (neutral)

**`GraphFeedbackStore`** (`Infrastructure.AI.KnowledgeGraph/Feedback`) — in-memory
- EMA formula: `newWeight = alpha * normalizedScore + (1 - alpha) * oldWeight`
- Normalization: scores (1-5) -> (0.0-1.0) via `(score - 1) / 4` clamped to [0, 1]
- Thread-safe via `ConcurrentDictionary`; tracks `UpdateCount` and `LastUpdatedAt`

**`ISkillEffectivenessTracker`** -> `GraphSkillEffectivenessTracker`
- Records skill outcomes as `SkillMetric` nodes in knowledge graph
- Synthetic `SkillClassification` index node links metrics for a classification
- Edge: `"tracks"` predicate; retrieval via `IKnowledgeGraphStore.GetNeighborsAsync()`

**Configuration (`GraphRagConfig`):**
- `FeedbackEnabled` (bool, default: false)
- `FeedbackAlpha` (double, default: 0.3)
- `SkillEffectivenessEnabled` (default: true)
- `SkillAmendmentsEnabled` (default: true)

**Existing Learnings Pattern:** `{runDir}/learnings.md` per MetaHarness run
- Pre-loop: `ReadLearningsFileAsync(runDir)` loads prior
- Post-iteration: `AppendLearningsAsync(runDir, entry)` via `BuildLearningsEntry(iteration, proposal, evalResult, accepted)`

### 3. Knowledge Graph Infrastructure

**`IKnowledgeGraphStore`** — keyed DI: `"in_memory"`, `"postgresql"`, `"neo4j"`, `"managed_code"`
- Default resolved from `AppConfig.AI.Rag.GraphRag.GraphProvider`
- Implementations: `InMemoryGraphStore`, `PostgreSqlGraphStore`, `Neo4jGraphStore`

**Compliance Layer:** `ComplianceAwareGraphStore` decorator
- Stamps temporal metadata, filters expired nodes, emits audit events
- Retention policies: Fact (365d), SkillMetric (180d), SkillAmendment (365d), Concept (730d)

**Provenance:** `IProvenanceStamper` -> `DefaultProvenanceStamper`
- Every extracted node/edge gets source pipeline, task, timestamp, extraction confidence
- Config: `GraphRagConfig.ProvenanceEnabled` (default: true)

### 4. Phase 2 Escalation Integration Points

**`IEscalationService`** (`Application.AI.Common/Interfaces/Escalation`)
- `RequestEscalationAsync(EscalationRequest)` — blocking
- `QueueEscalationAsync(EscalationRequest)` — non-blocking, returns ID
- `SubmitDecisionAsync(escalationId, ApproverDecision)` -> `EscalationOutcome?`
- `CancelEscalationAsync(escalationId, reason)`

**`DefaultEscalationService`** (`Infrastructure.AI/Escalation`)
- In-memory `ConcurrentDictionary<Guid, EscalationState>`
- Audit via `IEscalationAuditStore`, notifications via `IEscalationNotifier`
- Keyed DI approval strategies

### 5. AG-UI SSE Event Patterns

**`AgUiEscalationNotifier`** (`Presentation.AgentHub/Notifications`)
- Implements `IEscalationNotificationChannel`
- Translates domain records -> AG-UI SSE events via `IAgUiEventWriterAccessor.Writer`
- Events: `EscalationRequestedEvent`, `EscalationResolvedEvent`, `EscalationExpiringEvent`
- Graceful no-op when no AG-UI run active (ConsoleUI)
- `IEscalationNotifier` composes multiple `IEscalationNotificationChannel` implementations

### 6. DI Registration Patterns

- Keyed DI: `AddKeyedSingleton<IKnowledgeGraphStore>("in_memory", ...)` etc.
- Default resolution: `sp.GetRequiredKeyedService<T>(config.Provider)`
- Escalation: `AddSingleton<IEscalationService, DefaultEscalationService>` etc.
- Feedback: `AddSingleton<IFeedbackStore>(sp => new GraphFeedbackStore(...))`
- Evaluation: `AddScoped<IEvaluationService, AgentEvaluationService>`

### 7. Testing Setup

- **Frameworks:** xUnit, Moq, FluentAssertions, Microsoft.NET.Test.Sdk
- **TimeProvider:** `FakeTimeProvider` for deterministic time testing
- **DI in tests:** `ServiceCollection` + `BuildServiceProvider()` for keyed DI mocking
- **Test structure:** Mirror src layout with `*.Tests` suffix
- **EMA testing:** Normalization, blending, clamping, batch, edge feedback, defaults

### 8. Configuration Patterns

- Root: `AppConfig` bound via `IOptionsMonitor<AppConfig>` for dynamic reload
- Sections: `AppConfig.AI.Rag.GraphRag` -> `GraphRagConfig`, `AppConfig.AI.MetaHarness` -> `MetaHarnessConfig`, `AppConfig.AI.Governance`
- Pattern: `sp.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue`

### 9. Result<T> Pattern

- `Result`: `IsSuccess`, `Errors` (IReadOnlyList<string>), `FailureType` (ResultFailureType)
- Factory: `Success()`, `Fail(errors)`, `ValidationFailure(errors)`, `Unauthorized()`, `Forbidden()`, `ContentBlocked()`, `NotFound()`, `PermissionRequired()`, `GovernanceBlocked()`, `PendingApproval()`
- `Result<T>`: adds `T? Value`, implicit conversion from non-null value
- MediatR behaviors check `TResponse.IsAssignableFrom(Result)` and return failures

### 10. Domain Models — Governance & Escalation

**Records:** `EscalationRequest`, `EscalationOutcome`, `ApproverDecision`, `EscalationAuditRecord`
**Enums:** `RiskLevel`, `EscalationPriority`, `ApprovalStrategyType`, `EscalationResolutionType`, `EscalationTimeoutAction`, `EscalationWaitBehavior`, `AutonomyLevel`
**Governance:** `GovernanceDecision`, `AutonomyTierPolicy`

### Key Codebase Insights for Phase 3

1. Drift detection should listen to trace completion events; traces persist PassRate, costs, per-task results
2. Existing EMA model in `IFeedbackStore`/`GraphFeedbackStore` is the foundation for drift scoring
3. `learnings.md` per run in MetaHarness is the pattern for cross-run learning chains
4. Drift -> escalation via `IEscalationNotificationChannel` or SSE events
5. `GraphSkillEffectivenessTracker` pattern for storing drift records in knowledge graph
6. Keyed DI for pluggable drift detectors (statistical, heuristic, ML-based)
7. `Result<DriftReport>` with structured failure types
8. `IDriftAuditStore` modeled after `IEscalationAuditStore`

---

## Part 2: Web Research

### Topic 1: ML Model Drift Detection Patterns (2025-2026)

#### Approaches for LLM/Agent Output Drift

1. **Embedding-based Semantic Drift** — Track distribution of output embeddings over time. Cosine similarity between rolling windows. Flag when centroid/spread shifts beyond threshold.

2. **Statistical Distribution Tests** — KS tests and KL divergence on embedding distributions.

3. **LLM-as-Judge Scoring** — Separate evaluator LLM scores outputs on quality dimensions. Track scores over time. DeepEval v3.2 provides 50+ structured quality metrics.

4. **Behavioral Baseline Comparison** — Compare agent behavior to itself over time: reasoning structure, response entropy, input-output consistency.

5. **CUSUM Control Charts** — From Statistical Process Control. Cumulative sum of deviations from target mean. Formula: `S_i+ = max(0, S_{i-1}+ + (x_i - mu_0) - k)` where k ~ sigma/2. Alarm when `S_i+ > h`.

6. **Multi-Signal Correlation** — Correlate drift across retrieval pipelines, model components, infrastructure layers.

#### Baseline Establishment

- **Behavioral baselines:** Capture "normal" during a golden period
- **Per-segment baselines:** Different query types/task categories have different profiles
- **Rolling baselines:** 7-day, 30-day sliding windows (not fixed-point)
- **Reference dataset evaluation:** Curated input-output pairs, re-evaluated periodically

#### Scoring Dimensions

| Dimension | Description | Metric |
|---|---|---|
| Faithfulness | Output consistent with context | 0-1 score |
| Hallucination Rate | Fabricated facts | 0-1 score |
| Relevance | Addresses user intent | Cosine similarity |
| Coherence | Logical consistency | Multi-turn scoring |
| Factual Consistency | Alignment with known facts | BLEURT/FactScore |
| Structural Conformance | Matches expected format | Schema validation |
| Tool Usage Patterns | Correct tool selection/params | Accuracy vs expected |
| Instruction Following | System prompt adherence | Benchmark scoring |

Industry thresholds: Hallucination < 0.10 (alert >= 0.15), Faithfulness >= 0.85 (alert < 0.80), Relevance >= 0.90 (alert < 0.85)

#### Threshold Strategies

1. **Static** — Fixed values. Simple but alert-fatigue prone.
2. **Adaptive/Moving Average** — Alert when > 2 sigma below 30-day rolling mean.
3. **SPC (recommended):**
   - Shewhart: UCL/LCL at mean +/- 3 sigma. Large shifts.
   - CUSUM: Cumulative deviation. Small persistent shifts.
   - EWMA: Exponentially weighted moving average. Moderate shifts.
4. **Context-Aware** — Require convergent evidence (multiple signals dropping together).

#### Temporal Tracking

- Sliding window aggregation: 1-hour, 24-hour, 7-day
- Trend detection: Linear regression on rolling windows, flag negative slopes
- Entropy monitoring: Increasing entropy signals declining confidence
- Consistency monitoring: Increasing variance for similar inputs = instability

**Sources:**
- DasRoot: How to Monitor LLM Drift (Feb 2026)
- InsightFinder: Hidden Cost of LLM Drift (Oct 2025)
- Galileo: 7 Strategies for LLM Reliability (Jul 2025)
- arXiv: SPC for OOD Detection (Feb 2024, 2402.08088)
- AWS Prescriptive Guidance: Detecting Drift in Production

### Topic 2: Feedback-Weighted Retrieval and EMA Scoring

#### Blending Semantic + Feedback

Standard formula: `final_score = (1 - feedback_alpha) * semantic_similarity + feedback_alpha * feedback_score`
Typical `feedback_alpha`: 0.1-0.3 (semantic stays dominant).

#### EMA Learning Rates

- **0.1-0.15**: Conservative. Many signals needed. Best for factual knowledge.
- **0.2-0.3**: Moderate. Good default. Balances responsiveness/stability.
- **0.4-0.5**: Aggressive. Fast adaptation for volatile domains.
- **Bias-corrected EMA (BEMA):** `corrected = EMA / (1 - (1-alpha)^t)`. 30% fewer steps to target. Important for new nodes.

#### Feedback Decay

1. **Time-weighted EMA**: Recent feedback contributes more naturally, but doesn't actively discount old feedback.
2. **Weibull-based temporal decay** (arXiv Apr 2026): `freshness = exp(-(age / tau)^kappa)`. Type-aware shelf lives auto-discovered from data. ICU vitals: tau=2.4d, genomic facts: tau=5182d.
3. **Access-frequency decay**: Ebbinghaus forgetting curve applied to retrieval.

#### Preventing Echo Chambers

1. **Diversity injection**: 10-20% non-feedback-optimized results
2. **Exploration-exploitation balance**: Epsilon-greedy or UCB
3. **Feedback weight ceiling**: Max 30% influence from pure semantic score
4. **Counter-attitudinal injection**: Include results challenging dominant pattern
5. **Feedback source diversity**: Weight different users/sessions differently
6. **Periodic baseline reset**: Evaluate subset ignoring feedback to detect divergence
7. **Self-RAG/CRAG**: Critique retrieved results, detect degradation, trigger refinement

**Sources:**
- ZeroEntropy: Ultimate Guide to Reranking (2026)
- arXiv: Not All Memories Age the Same (Apr 2026, 2604.26970)
- DEV Community: Graph-Augmented Hybrid Retrieval
- NAACL 2025: KGR3 for KG Completion (2411.08165)

### Topic 3: Cross-Session Agent Memory Architectures

#### Leading API Patterns

**Pattern A — Cloudflare Agent Memory (Apr 2026):**
- Operations: `ingest`, `remember`, `recall`, `forget`, `list`
- Memory types: Facts, Events, Instructions, Tasks
- Two-pass ingestion with 8-check verifier

**Pattern B — Cognee (ECL Pipeline):**
- `.add()`, `.cognify()`, `.search()`
- OWL ontology validation (80% fuzzy cutoff)

**Pattern C — Hindsight (Dec 2025):**
- TEMPR: Retain + Recall (structured memory bank)
- CARA: Reflect (preference-conditioned reasoning)

#### Production Systems Compared

| System | Architecture | Benchmark | Trade-off |
|---|---|---|---|
| **Letta/MemGPT** | 3-tier (core/archival/recall), agent-managed paging | N/A | Hard to audit agent's memory choices |
| **Zep/Graphiti** | Temporal knowledge graph, validity windows | 63.8% LongMemEval | Hours of graph catch-up; 600K+ token footprint |
| **Mem0** | 4-scope (user/agent/run/app), 19 vector backends | Leads LOCOMO | Fastest to prod; staleness still open problem |
| **Cognee** | KG from unstructured, ontology validation | N/A | Multi-tenant; ~12K stars |

#### Graph-Backed Memory Patterns

1. **Vector-only**: Embed + similarity retrieve. Simple, weak on temporal/relational.
2. **Vector + Knowledge Graph**: Similarity AND entity/relationship traversal. Supports "what changed when."
3. **Tiered/Agent-managed**: Agent decides working memory vs. paged out.

Production backends: Neo4j (most mature), Kuzu (embedded), Neptune Analytics (AWS), FalkorDB (Redis-compat), PostgreSQL+AGE (emerging).

#### Correction Capture with Provenance

**PROV-AGENT (2025):** W3C PROV data model extended for agents. Nodes typed as prompts/actions/validation. Edges: temporal (chronological) + semantic (derived-from, resource-use).

**Learning Record Pattern:** IDs: `TYPE-YYYYMMDD-XXX`. Triggers: command failure, user correction, missing capability, outdated knowledge.

#### Decay and Freshness

**SSGM Framework (Mar 2026):** Governance middleware decoupling generative policy from memory. Four governed gates for read/write/forget.

**Adaptive Decay (Apr 2026):** Weibull-based `freshness = exp(-(age/tau)^kappa)`. Hierarchical: domain baseline -> context adjustment -> entity-specific. Auto-discovered shelf lives.

**Three forgetting signals:** Time decay (Ebbinghaus), access frequency, semantic importance (LLM-judged).

**Open problem:** Staleness in long-term memory. Mem0 2026 report: "a memory about a metric definition is relevant until it isn't, at which point it becomes confidently wrong."

#### Multi-Category Learning Types

| Category | Decay Profile |
|---|---|
| Facts | Long shelf life, event-triggered invalidation |
| Events | Medium decay, context-dependent |
| Instructions | Long, version-tracked |
| Tasks | Short, completion-triggered cleanup |
| Corrections | No decay (permanent learnings) |
| Preferences | EMA-weighted, user-scoped |

**Sources:**
- Fountain City: Agent Memory Systems Compared (Apr 2026)
- Vectorize: Mem0 vs Letta (2026)
- Mem0: State of AI Agent Memory 2026
- arXiv: Zep Temporal KG (2501.13956)
- arXiv: SSGM Framework (2603.11768)
- arXiv: Not All Memories Age the Same (2604.26970)
- Cloudflare: Agent Memory (Apr 2026)

---

## Synthesis: Key Decisions for Phase 3

### Drift Detection Approach
- **SPC/CUSUM + EWMA** for threshold-free drift detection (self-calibrating from data)
- **Multi-dimensional scoring**: faithfulness, relevance, structural conformance, tool usage accuracy
- **Rolling baselines** (7d/30d windows) rather than fixed-point
- **Escalation integration**: drift beyond threshold triggers Phase 2 escalation

### Learnings Log Approach
- **Typed memories** (Facts/Events/Instructions/Corrections/Preferences) with type-specific decay
- **Bias-corrected EMA** for new nodes with few feedback signals
- **Weibull-inspired decay** (simplified: 3 categories — volatile/stable/permanent)
- **Echo chamber prevention**: diversity injection + feedback weight ceiling
- **PROV-AGENT-style provenance**: W3C PROV-inspired correction records

### Architecture Alignment
- Both subsystems follow existing keyed DI, Result<T>, MediatR pipeline patterns
- Drift audit modeled after `IEscalationAuditStore`
- AG-UI SSE events modeled after `AgUiEscalationNotifier`
- Graph storage via `IKnowledgeGraphStore` for learning persistence
- Config via `GraphRagConfig` extension or new `DriftConfig`/`LearningsConfig` sections

# Implementation Plan: Phase 3 — Quality Loop (Drift Detection + Learnings Log)

## Context

The Microsoft Agentic Harness is a production-grade .NET 10 template for Microsoft Agent Framework agents, built on Clean Architecture with CQRS/MediatR, keyed DI, and FluentValidation. It already has:

- **Evaluation infrastructure:** `IEvaluationService`, `HarnessScores` (PassRate, per-task results), `IExecutionTraceStore` with filesystem traces
- **Feedback infrastructure:** `IFeedbackStore` with EMA-weighted scoring (`GraphFeedbackStore`), `ISkillEffectivenessTracker`
- **Knowledge graph:** `IKnowledgeGraphStore` with keyed DI (in_memory, postgresql, neo4j), compliance decorator, provenance stamping
- **Escalation (Phase 2):** `IEscalationService`, `IEscalationAuditStore`, AG-UI SSE events via `AgUiEscalationNotifier`
- **Patterns:** `Result<T>` error handling, `IOptionsMonitor<AppConfig>` for config, immutable records, `TimeProvider` injection + `FakeTimeProvider` for testing
- **Conventions:** All new services inject `TimeProvider` (not `DateTimeOffset.UtcNow`) for testability. OpenTelemetry metrics via static `*Metrics` classes. Services check `Enabled` config flag with early-return no-op when disabled.

Phase 3 adds two interconnected subsystems that close the feedback loop: drift detection identifies quality regressions, learnings log captures corrections and amplifies what works.

---

## Section 1: Drift Detection Domain Models

**Layer:** Domain.AI

Create the domain vocabulary for drift detection. All types are immutable records with init-only properties.

### Types to Create

**`DriftDimension`** — enum defining quality scoring dimensions:
- Faithfulness, Relevance, StructuralConformance, ToolUsageAccuracy, Coherence, InstructionFollowing

**`DriftSeverity`** — enum with tiered response semantics:
- None, Warn, Alert, Escalate

**`DriftScope`** — enum for baseline hierarchy levels:
- Agent, Skill, TaskType

**`DriftDimensionScore`** — record holding current vs baseline comparison for one dimension:
- `CurrentValue` (double), `BaselineValue` (double), `EwmaValue` (double), `Deviation` (double — in sigma units)

**`DriftBaseline`** — record representing a "known good" quality snapshot:
- `BaselineId` (Guid), `Scope` (DriftScope), `ScopeIdentifier` (string)
- `Dimensions` (IReadOnlyDictionary<DriftDimension, double>), `DimensionSigmas` (IReadOnlyDictionary<DriftDimension, double> — standard deviation per dimension)
- `SampleCount` (int), `WindowStart`/`WindowEnd` (DateTimeOffset), `CreatedAt` (DateTimeOffset)

**`DriftScore`** — record for a single evaluation's drift measurement:
- `ScoreId` (Guid), `BaselineId` (Guid), `Scope`/`ScopeIdentifier`
- `Dimensions` (IReadOnlyDictionary<DriftDimension, DriftDimensionScore>)
- `OverallDrift` (double — max deviation across dimensions), `Severity` (DriftSeverity)
- `ScoredAt` (DateTimeOffset)

**`DriftEvent`** — record for a detected drift occurrence (persisted as graph node):
- `EventId` (Guid), `DriftScore` (DriftScore), `Resolution` (DriftResolution?), `DetectedAt` (DateTimeOffset)

**`DriftResolution`** — record:
- `ResolvedBy` (DriftResolutionType: LearningApplied, BaselineAdjusted, ManualDismissal, EscalationResolved)
- `ResolutionId` (string — learning ID, escalation ID, etc.), `ResolvedAt` (DateTimeOffset)

**`DriftAuditRecord`** — record for compliance trail:
- `RecordId` (Guid), `EventId` (Guid), `RecordType` (DriftAuditRecordType: Detected, Resolved, BaselineUpdated, EscalationTriggered)
- `Data` (string — JSON payload), `RecordedAt` (DateTimeOffset)

**File organization:** `Domain.AI/DriftDetection/` directory with one file per type or logical group.

---

## Section 2: Learnings Log Domain Models

**Layer:** Domain.AI

Create the domain vocabulary for the learnings system.

### Types to Create

**`LearningCategory`** — enum:
- FactualCorrection, StylePreference, ToolUsagePattern, DomainKnowledge, InstructionUpdate

**`DecayClass`** — enum:
- Volatile, Stable, Permanent

**`LearningSourceType`** — enum:
- HumanCorrection, DriftDetection, EscalationResolution, AgentSelfImprovement, ManualEntry

**`LearningScope`** — record for hierarchical 3-tier scoping:
- `AgentId` (string?), `TeamId` (string?), `IsGlobal` (bool)
- Scope resolution: agent > team > global. A query for agent "X" in team "T" retrieves X-specific, T-team, and global learnings.

**`LearningSource`** — record for provenance:
- `SourceType` (LearningSourceType), `SourceId` (string), `SourceDescription` (string)

**`LearningProvenance`** — record:
- `OriginPipeline` (string), `OriginTask` (string), `OriginTimestamp` (DateTimeOffset), `Confidence` (double 0.0-1.0)

**`LearningEntry`** — the core learning record:
- `LearningId` (Guid), `Category` (LearningCategory), `DecayClass` (DecayClass)
- `Scope` (LearningScope), `Content` (string), `Source` (LearningSource), `Provenance` (LearningProvenance)
- `FeedbackWeight` (double, default 1.0), `UpdateCount` (int)
- `CreatedAt`/`LastAccessedAt`/`LastReinforcedAt` (DateTimeOffset)

**`WeightedLearning`** — record returned by recall:
- `Learning` (LearningEntry)
- `RelevanceScore` (double), `FeedbackScore` (double), `FreshnessScore` (double), `FinalScore` (double)

**File organization:** `Domain.AI/Learnings/` directory.

---

## Section 3: Drift Detection Configuration

**Layer:** Domain.Common (config), Infrastructure (binding)

### DriftConfig

New configuration section at `AppConfig.AI.DriftDetection`:

- `Enabled` (bool, default: true)
- `EwmaLambda` (double, default: 0.2) — EWMA smoothing factor
- `ControlLimitWidth` (double, default: 3.0) — sigma multiplier for control limits
- `MinSamplesForBaseline` (int, default: 20) — minimum evaluations before baseline is valid
- `BaselineWindowDays` (int, default: 7) — rolling window for baseline recalculation
- `WarnThresholdSigma` (double, default: 1.5)
- `AlertThresholdSigma` (double, default: 2.5)
- `EscalateThresholdSigma` (double, default: 3.0)
- `EscalationEnabled` (bool, default: true) — whether escalation severity triggers Phase 2

### appsettings.json

Add `DriftDetection` block under `AI` section, following the same pattern as `Governance.Escalation` and `Governance.Resilience` blocks from Phase 2.

### FluentValidation

`DriftConfigValidator` — validate that thresholds are ordered (Warn < Alert < Escalate), lambda in (0, 1], MinSamples > 0, etc.

---

## Section 4: Learnings Log Configuration

**Layer:** Domain.Common (config), Infrastructure (binding)

### LearningsConfig

New configuration section at `AppConfig.AI.Learnings`:

- `Enabled` (bool, default: true)
- `FeedbackAlpha` (double, default: 0.25) — feedback weight in blending
- `FeedbackCeiling` (double, default: 0.3) — max feedback influence
- `DiversityInjectionRatio` (double, default: 0.15)
- `VolatileShelfLifeDays` (int, default: 7)
- `StableShelfLifeDays` (int, default: 180)
- `PruneIntervalHours` (int, default: 24)
- `BaselineAdjustmentThreshold` (double, default: 0.8) — min feedback weight to trigger drift baseline adjustment
- `BiasCorrection` (bool, default: true) — enable bias-corrected EMA for new learnings

### appsettings.json

Add `Learnings` block under `AI` section.

### FluentValidation

`LearningsConfigValidator` — validate alpha in (0, 1], ceiling in (0, 1], diversity ratio in [0, 0.5], shelf lives > 0.

---

## Section 5: Drift Detection Application Interfaces

**Layer:** Application.AI.Common

Define the contracts that infrastructure implements.

### Interfaces

**`IDriftDetectionService`** — orchestrates drift evaluation:
- `EvaluateDriftAsync(DriftEvaluationRequest, CancellationToken)` -> `Result<DriftScore>`
- `GetBaselineAsync(DriftScope, string scopeIdentifier, CancellationToken)` -> `Result<DriftBaseline?>`
- `UpdateBaselineAsync(DriftBaselineUpdateRequest, CancellationToken)` -> `Result<DriftBaseline>`
- `GetDriftHistoryAsync(DriftHistoryQuery, CancellationToken)` -> `Result<IReadOnlyList<DriftScore>>`

**`IDriftBaselineStore`** — baseline persistence:
- `SaveBaselineAsync(DriftBaseline, CancellationToken)` -> `Result`
- `GetBaselineAsync(DriftScope, string scopeIdentifier, CancellationToken)` -> `Result<DriftBaseline?>`
- `GetBaselinesAsync(DriftScope?, CancellationToken)` -> `Result<IReadOnlyList<DriftBaseline>>`

**`IDriftScorer`** — keyed DI for pluggable scoring strategies:
- `ScoreDimensionAsync(DriftDimension, double currentValue, DriftBaseline baseline, CancellationToken)` -> `Result<DriftDimensionScore>`
- Default key: `"ewma"`

**`IDriftAuditStore`** — compliance records:
- `RecordAsync(DriftAuditRecord, CancellationToken)` -> `Result`
- `GetRecordsAsync(DriftAuditQuery, CancellationToken)` -> `Result<IReadOnlyList<DriftAuditRecord>>`

**`IDriftNotificationChannel`** — individual channel (modeled after `IEscalationNotificationChannel`):
- `NotifyDriftDetectedAsync(DriftScore, CancellationToken)` -> `Task`
- `NotifyDriftResolvedAsync(DriftEvent, CancellationToken)` -> `Task`

**`IDriftNotifier`** — composite dispatcher (modeled after `IEscalationNotifier`):
- Same methods as `IDriftNotificationChannel`, fans out to all registered channels
- Separates the composite interface from individual channels for clean DI resolution

**`IEwmaStateStore`** — EWMA running state persistence:
- `GetStateAsync(DriftScope, string scopeIdentifier, DriftDimension, CancellationToken)` -> `EwmaState?`
- `SaveStateAsync(EwmaState, CancellationToken)` -> `Result`
- `GetStatesAsync(DriftScope, string scopeIdentifier, CancellationToken)` -> `IReadOnlyList<EwmaState>`

**`EwmaState`** — record for EWMA running state (mutable across evaluations, separate from immutable baseline):
- `Scope` (DriftScope), `ScopeIdentifier` (string), `Dimension` (DriftDimension)
- `CurrentEwma` (double), `SampleCount` (int), `LastUpdatedAt` (DateTimeOffset)

**Request/Query DTOs:** `DriftEvaluationRequest`, `DriftBaselineUpdateRequest`, `DriftHistoryQuery`, `DriftAuditQuery` — all immutable records with FluentValidation.

---

## Section 6: Learnings Log Application Interfaces

**Layer:** Application.AI.Common

### Interfaces

**`ILearningsStore`** — learning persistence (keyed DI: `"graph"`, `"in_memory"`):
- `SaveAsync(LearningEntry, CancellationToken)` -> `Result`
- `GetAsync(Guid learningId, CancellationToken)` -> `Result<LearningEntry?>`
- `SearchAsync(LearningSearchCriteria, CancellationToken)` -> `Result<IReadOnlyList<LearningEntry>>`
- `UpdateAsync(LearningEntry, CancellationToken)` -> `Result`
- `SoftDeleteAsync(Guid learningId, string reason, CancellationToken)` -> `Result`

**`ILearningDecayService`** — temporal decay calculation:
- `CalculateFreshnessAsync(LearningEntry, CancellationToken)` -> `double`
- `PruneExpiredAsync(CancellationToken)` -> `Result<int>`

**`ILearningNotificationChannel`** — AG-UI events:
- `NotifyLearningCapturedAsync(LearningEntry, CancellationToken)` -> `Task`
- `NotifyLearningAppliedAsync(LearningEntry, string agentId, CancellationToken)` -> `Task`

### MediatR Commands and Queries

**`RememberCommand`** -> `Result<LearningEntry>`:
- Input: content, category, scope, source, provenance
- Validation: content not empty, category valid, scope has at least one identifier or IsGlobal
- Handler: create LearningEntry, save to store, emit notification

**`RecallQuery`** -> `Result<IReadOnlyList<WeightedLearning>>`:
- Input: context string (what to recall for), scope (agent/team), maxResults, minRelevance
- Handler: search store by scope hierarchy, compute freshness/feedback/relevance scores, blend, inject diversity, apply ceiling, return top-K

**`ForgetCommand`** -> `Result`:
- Input: learningId, reason
- Handler: soft delete with audit trail

**`ImproveLearningCommand`** -> `Result<LearningEntry>`:
- Input: learningId, feedbackScore (1.0-5.0), reinforcementContent (optional)
- Handler: apply EMA to feedback weight, update reinforced timestamp, increment update count

---

## Section 7: EWMA Drift Scorer Implementation

**Layer:** Infrastructure.AI

The core drift scoring engine using Exponentially Weighted Moving Average.

### EwmaDriftScorer

Implements `IDriftScorer` with key `"ewma"`.

**EWMA calculation:**
- `EWMA_t = lambda * x_t + (1 - lambda) * EWMA_{t-1}`
- `lambda` from `DriftConfig.EwmaLambda` (default 0.2)
- Initial EWMA = baseline mean for the dimension

**Control limits:**
- `UCL = baseline_mean + L * sigma * sqrt(lambda / (2 - lambda))`
- `LCL = baseline_mean - L * sigma * sqrt(lambda / (2 - lambda))`
- `L` from `DriftConfig.ControlLimitWidth` (default 3.0)
- `sigma` from `DriftBaseline.DimensionVariances[dimension]`

**Deviation calculation:**
- `deviation = abs(ewma - baseline_mean) / sigma`
- This deviation in sigma units drives severity classification

**State management:** EWMA state persisted via `IEwmaStateStore`. On each evaluation:
1. Load `EwmaState` for scope+dimension (or initialize from baseline mean if first evaluation)
2. Compute new EWMA: `newEwma = lambda * currentValue + (1 - lambda) * state.CurrentEwma`
3. Save updated `EwmaState` with incremented `SampleCount` and new `LastUpdatedAt` via `TimeProvider`
4. Return `DriftDimensionScore` with the new EWMA value and deviation

`EwmaState` stored as graph nodes with deterministic IDs: `"ewma:{scope}:{identifier}:{dimension}"` for direct lookup via `IKnowledgeGraphStore.GetNodeAsync()`.

### Severity Classifier

Pure function: takes deviation (in sigma) and DriftConfig thresholds, returns DriftSeverity:
- `< WarnThresholdSigma` -> None
- `< AlertThresholdSigma` -> Warn
- `< EscalateThresholdSigma` -> Alert
- `>= EscalateThresholdSigma` -> Escalate

---

## Section 8: Drift Detection Service Implementation

**Layer:** Infrastructure.AI

### DefaultDriftDetectionService

Implements `IDriftDetectionService`. Injects `TimeProvider`, `IOptionsMonitor<AppConfig>` for `DriftConfig`. Orchestrates the full drift evaluation pipeline.

**`Enabled` guard:** All public methods check `DriftConfig.Enabled` first. If disabled, return `Result.Success` with default/empty values.

**`EvaluateDriftAsync` flow:**
1. Resolve baseline for the given scope/identifier (fall back to parent scope if missing)
2. For each dimension in the evaluation request, invoke `IDriftScorer.ScoreDimensionAsync`
3. Compute overall drift as max deviation across dimensions
4. Classify severity using threshold config
5. If severity >= Warn, create `DriftEvent` and persist as graph node
6. If severity == Escalate and `DriftConfig.EscalationEnabled`, call `IEscalationService.QueueEscalationAsync`
7. Record audit via `IDriftAuditStore`
8. Emit notification via `IDriftNotifier` (composite pattern, fans out to all `IDriftNotificationChannel` implementations)
9. Return `Result<DriftScore>`

**Baseline fallback hierarchy:**
- TaskType baseline -> Skill baseline -> Agent baseline
- If no baseline exists at any level, return `Result.Fail("No baseline available")`

**`UpdateBaselineAsync` flow:**
1. Query drift history for the scope within the rolling window
2. Compute mean and variance per dimension from the history
3. Create new `DriftBaseline` record
4. Save via `IDriftBaselineStore`
5. Record audit

### Graph Integration

Drift events stored as knowledge graph nodes:
- Node type: `"DriftEvent"`, properties: serialized DriftScore, severity, scope
- Edge `"affects"` to scope identifier node
- Edge `"resolved_by"` to learning entry node (when resolved)
- Uses existing `IKnowledgeGraphStore` via DI

### CompositeDriftNotifier

`CompositeDriftNotifier` implementing `IDriftNotifier`, composing multiple `IDriftNotificationChannel` implementations (AG-UI, logging, future: Slack/Teams). Same pattern as `CompositeEscalationNotifier`.

### DriftMetrics

Static class with OpenTelemetry counters and histograms:
- `drift.evaluations.total` (counter, tagged by scope + severity)
- `drift.escalations.triggered` (counter)
- `drift.baselines.updated` (counter)
- `drift.evaluation.duration_ms` (histogram)
Following `EscalationMetrics` pattern with `DriftConventions` for semantic conventions.

---

## Section 9: Drift Baseline Store

**Layer:** Infrastructure.AI

### GraphDriftBaselineStore

Implements `IDriftBaselineStore` using `IKnowledgeGraphStore`.

Baselines stored as graph nodes with deterministic IDs for direct lookup (no full-scan queries needed):
- Node ID: `"driftbaseline:{scope}:{identifier}"` (e.g., `"driftbaseline:skill:code_review"`)
- Node type: `"DriftBaseline"`, properties: serialized baseline data
- Edge `"baseline_for"` to scope identifier node (agent/skill/task-type)
- Lookup: `IKnowledgeGraphStore.GetNodeAsync("driftbaseline:{scope}:{identifier}")` — O(1)

Current baseline overwrites the node (same deterministic ID). Previous baselines available via drift audit trail (`DriftAuditRecordType.BaselineUpdated`).

### InMemoryDriftBaselineStore

Testing implementation. `ConcurrentDictionary<(DriftScope, string), DriftBaseline>`.

---

## Section 10: Drift Audit Store

**Layer:** Infrastructure.AI

### JsonlDriftAuditStore

Implements `IDriftAuditStore`. Append-only JSONL file per day (same pattern as `JsonlEscalationAuditStore`).

- File path: `{configuredAuditPath}/drift-audit/{yyyy-MM-dd}.jsonl`
- Each line: JSON-serialized `DriftAuditRecord`
- Thread-safe writes via `SemaphoreSlim`
- Query: scan files in date range, deserialize, filter by record type / event ID

---

## Section 11: Learning Decay Service

**Layer:** Infrastructure.AI

### DefaultLearningDecayService

Implements `ILearningDecayService`.

**`CalculateFreshnessAsync`:**
- Look up the learning's `DecayClass`
- Get shelf life from `LearningsConfig` (Volatile -> days, Stable -> days, Permanent -> infinity)
- Calculate age: `now - LastReinforcedAt` (or `CreatedAt` if never reinforced)
- Freshness: `max(0, 1 - (age.TotalDays / shelfLifeDays))`
- Permanent: always returns 1.0
- Bias-corrected: if `LearningsConfig.BiasCorrection` enabled and `UpdateCount < 5`, apply correction factor `1 / (1 - (1 - alpha)^updateCount)` clamped to [0, 1]

**`PruneExpiredAsync`:**
- Query all learnings with `DecayClass != Permanent`
- For each, calculate freshness
- Soft-delete any with freshness <= 0
- Return count pruned

### LearningsPruningBackgroundService

`BackgroundService` that calls `ILearningDecayService.PruneExpiredAsync()` on a timer based on `LearningsConfig.PruneIntervalHours`. Registered conditionally when `Learnings.Enabled` is true. Uses `PeriodicTimer` with `TimeProvider` for testability. Logs pruned count at Information level.

---

## Section 12: Learnings Store (Graph-Backed)

**Layer:** Infrastructure.AI.KnowledgeGraph

### GraphLearningsStore

Implements `ILearningsStore` with key `"graph"`, backed by `IKnowledgeGraphStore`.

**Storage model:**
- Node ID: `"learning:{learningId}"` — deterministic for direct lookup
- Node type: `"LearningEntry"`, properties: serialized learning data including scope, category, decay class
- **Synthetic index nodes** (following `GraphSkillEffectivenessTracker` pattern):
  - `"learningindex:agent:{agentId}"` — collects all learnings for an agent
  - `"learningindex:team:{teamId}"` — collects all learnings for a team
  - `"learningindex:global"` — collects all global learnings
- Edge `"has_learning"` from index node to learning node
- Edge `"source_from"` to source entity (escalation, drift event, etc.)

**`SearchAsync` with scope hierarchy:**
1. Get agent index node neighbors (if AgentId provided): `GetNeighborsAsync("learningindex:agent:{agentId}")`
2. Get team index node neighbors (if TeamId provided): `GetNeighborsAsync("learningindex:team:{teamId}")`
3. Get global index node neighbors: `GetNeighborsAsync("learningindex:global")`
4. Merge, deduplicate by LearningId
5. Filter out soft-deleted (check `Properties["IsDeleted"]`)
6. Apply additional criteria filters (category, date range, min feedback weight)

**Soft delete:** Set `Properties["IsDeleted"] = true` and `Properties["DeleteReason"]` on the graph node. Node stays in graph for audit. `SearchAsync` filters out deleted nodes.

### InMemoryLearningsStore

Testing implementation with key `"in_memory"`. `ConcurrentDictionary<Guid, LearningEntry>`.

---

## Section 13: MediatR Command Handlers

**Layer:** Application.Core/CQRS/Learnings/ (following existing handler placement pattern)

### RememberCommandHandler

1. Validate input via FluentValidation pipeline behavior
2. Determine `DecayClass` from `LearningCategory` if not explicitly provided (FactualCorrection -> Permanent, others -> Stable)
3. Create `LearningEntry` with Guid, timestamps, default FeedbackWeight = 1.0
4. Save via `ILearningsStore`
5. Emit `ILearningNotificationChannel.NotifyLearningCapturedAsync`
6. Return `Result<LearningEntry>.Success(entry)`

### RecallQueryHandler

The recall pipeline is the most complex handler — it blends semantic relevance with feedback and freshness.

1. Validate input
2. Search store by scope hierarchy (agent -> team -> global)
3. For each candidate learning:
   - `relevanceScore`: semantic similarity via `IEmbeddingService` — embed query context and learning content, compute cosine similarity (existing RAG pipeline infrastructure)
   - `feedbackScore`: the learning's `FeedbackWeight` (already EMA-weighted)
   - `freshnessScore`: via `ILearningDecayService.CalculateFreshnessAsync`
   - `finalScore = (1 - alpha) * relevance + alpha * min(feedback * freshness, ceiling)`
4. Sort by finalScore descending
5. Apply diversity injection: replace bottom 15% with random non-feedback-optimized results (min 2 results before activation)
6. Fire-and-forget `RecordLearningAccessCommand` to update `LastAccessedAt` on recalled learnings (CQRS-clean: query doesn't mutate)
7. Return top-K as `WeightedLearning` records

### ForgetCommandHandler

1. Validate input
2. Call `ILearningsStore.SoftDeleteAsync(learningId, reason)`
3. Return `Result.Success()`

### ImproveLearningCommandHandler

1. Validate input (feedbackScore 1.0-5.0)
2. Retrieve learning via store
3. Apply EMA: `newWeight = alpha * normalize(score) + (1 - alpha) * oldWeight`
4. If `LearningsConfig.BiasCorrection` and `UpdateCount < 5`: apply bias correction
5. Update `FeedbackWeight`, increment `UpdateCount`, set `LastReinforcedAt`
6. If `FeedbackWeight >= LearningsConfig.BaselineAdjustmentThreshold`: signal drift baseline adjustment needed (create a domain event or direct call)
7. Save via store
8. Return `Result<LearningEntry>.Success(updated)`

### LearningsMetrics

Static class with OpenTelemetry counters and histograms:
- `learnings.remembered.total` (counter, tagged by category + scope)
- `learnings.recalled.total` (counter)
- `learnings.forgotten.total` (counter)
- `learnings.improved.total` (counter)
- `learnings.pruned.total` (counter)
- `learnings.recall.duration_ms` (histogram)
Following `EscalationMetrics` pattern with `LearningConventions`.

---

## Section 14: AG-UI Drift Events and SSE Notifier

**Layer:** Presentation.AgentHub (notifier), Domain.AI (event types)

### Event Types

Define AG-UI event DTOs (same namespace/pattern as escalation events):

- `DriftWarnEvent`: scope, scopeIdentifier, dimensions (dict), maxDeviation, severity
- `DriftAlertEvent`: same as warn + baselineId
- `DriftEscalateEvent`: same as alert + escalationId
- `DriftResolvedEvent`: eventId, resolution type, resolvedBy, resolvedAt

### AgUiDriftNotifier

Implements `IDriftNotificationChannel`. Same pattern as `AgUiEscalationNotifier`:
- Injects `IAgUiEventWriterAccessor`
- Translates domain drift events to AG-UI SSE event DTOs
- Graceful no-op when no active AG-UI run
- Catches exceptions (not `OperationCanceledException`), logs warnings

---

## Section 15: AG-UI Learning Events and SSE Notifier

**Layer:** Presentation.AgentHub (notifier), Domain.AI (event types)

### Event Types

- `LearningCapturedEvent`: learningId, category, scope, source description
- `LearningAppliedEvent`: learningId, agentId, context summary
- `LearningForgottenEvent`: learningId, reason

### AgUiLearningNotifier

Implements `ILearningNotificationChannel`. Same graceful no-op pattern.

---

## Section 16: Drift -> Escalation Integration

**Layer:** Infrastructure.AI

### Integration Point

When `DefaultDriftDetectionService.EvaluateDriftAsync` determines severity == Escalate:

1. Build an `EscalationRequest`:
   - `AgentId` from drift scope
   - `ToolName`: `"drift_detection"` (or the tool that produced the drifted output)
   - `Description`: summarize which dimensions drifted and by how much
   - `RiskLevel`: map DriftSeverity.Escalate -> RiskLevel.High
   - `ApprovalStrategy`: from config (default: AnyOf)
   - `Approvers`: from config (default: empty = all)

2. Call `IEscalationService.QueueEscalationAsync(request)`

3. Store the escalation ID on the `DriftEvent` for correlation

4. When escalation resolves (listen via `IEscalationNotificationChannel` or poll), update drift event resolution and optionally create a learning entry

### DriftEscalationBridge

A concrete `IEscalationNotificationChannel` implementation that bridges escalation resolutions back into the drift/learnings system:

- Registered as an escalation notification channel alongside `AgUiEscalationNotifier`
- In `NotifyEscalationResolvedAsync`: filters by `ToolName == "drift_detection"` convention
- When a drift-originated escalation resolves:
  1. Update the corresponding `DriftEvent` resolution via `IDriftDetectionService`
  2. If resolution includes corrections, build `RememberCommand` with `SourceType.EscalationResolution`, `SourceId` = escalation ID
  3. Execute via MediatR — the learning is automatically scoped, categorized, and weighted

---

## Section 17: Drift -> Learnings Integration

**Layer:** Infrastructure.AI (or Application, depending on where the orchestration lives)

### Baseline Adjustment from Learnings

When `ImproveLearningCommand` detects that a learning's FeedbackWeight exceeds `BaselineAdjustmentThreshold`:

1. Check if the learning is associated with a drift event (via `Source.SourceType == DriftDetection`)
2. If so, retrieve the drift event's scope and affected dimensions
3. Call `IDriftDetectionService.UpdateBaselineAsync` for the affected scope
4. Record baseline adjustment in drift audit
5. Resolve the drift event with `DriftResolutionType.BaselineAdjusted`

This prevents false-positive drift alerts when intentional changes are made and subsequently validated through feedback.

---

## Section 18: DI Registration

**Layer:** Infrastructure.AI (DependencyInjection.cs), Infrastructure.AI.KnowledgeGraph (DependencyInjection.cs), Presentation.AgentHub (DependencyInjection.cs)

### AIConfig.cs Update

Add two new properties to `AIConfig`:
- `DriftDetection` (DriftDetectionConfig)
- `Learnings` (LearningsConfig)

### Infrastructure.AI Registration

```
// Drift Detection
IDriftDetectionService -> DefaultDriftDetectionService (Singleton)
IDriftScorer [keyed "ewma"] -> EwmaDriftScorer (Singleton)
IDriftBaselineStore [keyed "graph"] -> GraphDriftBaselineStore (Singleton)
IDriftBaselineStore [keyed "in_memory"] -> InMemoryDriftBaselineStore (Singleton)
IDriftBaselineStore [default] -> resolved from config
IDriftAuditStore -> JsonlDriftAuditStore (Singleton)
IDriftNotifier -> CompositeDriftNotifier (Singleton)
IEwmaStateStore -> GraphEwmaStateStore (Singleton)

// Learnings
ILearningDecayService -> DefaultLearningDecayService (Singleton)
LearningsPruningBackgroundService -> (registered conditionally when Learnings.Enabled)
ILearningNotificationChannel -> (registered in Presentation)

// Drift-Escalation Bridge
IEscalationNotificationChannel -> DriftEscalationBridge (added to composite)
```

### Infrastructure.AI.KnowledgeGraph Registration

```
ILearningsStore [keyed "graph"] -> GraphLearningsStore (Singleton)
ILearningsStore [keyed "in_memory"] -> InMemoryLearningsStore (Singleton)
ILearningsStore [default] -> resolved from config (default: "graph")
```

### Presentation.AgentHub Registration

```
IDriftNotificationChannel -> AgUiDriftNotifier (added to composite)
ILearningNotificationChannel -> AgUiLearningNotifier (Singleton)
```

### MediatR

Command handlers auto-discovered via assembly scanning (existing pattern). FluentValidation validators likewise auto-discovered.

---

## Section 19: appsettings.json Configuration

Add the configuration blocks for both subsystems to `appsettings.json` and `appsettings.Development.json`.

### Structure

Under `AI`:
```
"DriftDetection": {
  "Enabled": true,
  "EwmaLambda": 0.2,
  "ControlLimitWidth": 3.0,
  "MinSamplesForBaseline": 20,
  "BaselineWindowDays": 7,
  "WarnThresholdSigma": 1.5,
  "AlertThresholdSigma": 2.5,
  "EscalateThresholdSigma": 3.0,
  "EscalationEnabled": true,
  "AuditPath": "data/audit"
},
"Learnings": {
  "Enabled": true,
  "StoreProvider": "graph",
  "FeedbackAlpha": 0.25,
  "FeedbackCeiling": 0.3,
  "DiversityInjectionRatio": 0.15,
  "VolatileShelfLifeDays": 7,
  "StableShelfLifeDays": 180,
  "PruneIntervalHours": 24,
  "BaselineAdjustmentThreshold": 0.8,
  "BiasCorrection": true
}
```

---

## Section 20: Full Test Suite Verification

Run `dotnet build` and `dotnet test` across the entire solution to verify:
- All new types compile
- All new tests pass
- No regressions in existing Phase 1/2 tests
- Coverage meets 80% threshold on new code

---

## Test Strategy (per section)

Each section includes its own tests written TDD-style. Key test areas:

- **Domain models (Sections 1-2):** Construction, immutability, equality, edge cases (empty dimensions, null scopes)
- **Config (Sections 3-4):** Validation rules, default values, binding from JSON
- **EWMA scorer (Section 7):** Mathematical correctness — known input/output pairs, edge cases (zero variance, single sample), state persistence
- **Drift service (Section 8):** Pipeline flow (mock scorer, store, notifier), severity classification, baseline fallback hierarchy, escalation trigger
- **Baseline store (Section 9):** CRUD, scope queries, graph node serialization
- **Audit store (Section 10):** Append, query by date range, thread safety
- **Decay service (Section 11):** Freshness calculation per decay class, pruning, bias correction math
- **Learnings store (Section 12):** CRUD, scope hierarchy search, soft delete
- **MediatR handlers (Section 13):** Remember/Recall/Forget/Improve pipelines with in-memory stores
- **SSE notifiers (Sections 14-15):** Event emission, graceful no-op when no writer
- **Integration (Sections 16-17):** Drift -> escalation trigger, learning -> baseline adjustment
- **DI (Section 18):** Service resolution, keyed DI, config binding

---

## File Organization

```
src/Content/Domain/Domain.AI/
  DriftDetection/
    DriftDimension.cs
    DriftSeverity.cs
    DriftScope.cs
    DriftDimensionScore.cs
    DriftBaseline.cs
    DriftScore.cs
    DriftEvent.cs
    DriftResolution.cs
    DriftAuditRecord.cs
  Learnings/
    LearningCategory.cs
    DecayClass.cs
    LearningSourceType.cs
    LearningScope.cs
    LearningSource.cs
    LearningProvenance.cs
    LearningEntry.cs
    WeightedLearning.cs

src/Content/Domain/Domain.Common/Config/AI/
  DriftDetectionConfig.cs
  LearningsConfig.cs

src/Content/Application/Application.AI.Common/Interfaces/
  DriftDetection/
    IDriftDetectionService.cs
    IDriftBaselineStore.cs
    IDriftScorer.cs
    IDriftAuditStore.cs
    IDriftNotificationChannel.cs
  Learnings/
    ILearningsStore.cs
    ILearningDecayService.cs
    ILearningNotificationChannel.cs

src/Content/Application/Application.AI.Common/
  Commands/DriftDetection/
    (DTOs: DriftEvaluationRequest, DriftBaselineUpdateRequest, etc.)
  Commands/Learnings/
    RememberCommand.cs
    RecallQuery.cs
    ForgetCommand.cs
    ImproveLearningCommand.cs

src/Content/Infrastructure/Infrastructure.AI/
  DriftDetection/
    DefaultDriftDetectionService.cs
    EwmaDriftScorer.cs
    GraphDriftBaselineStore.cs
    InMemoryDriftBaselineStore.cs
    JsonlDriftAuditStore.cs
    CompositeDriftNotifier.cs
  Learnings/
    DefaultLearningDecayService.cs

src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/
  Learnings/
    GraphLearningsStore.cs
    InMemoryLearningsStore.cs

src/Content/Presentation/Presentation.AgentHub/
  Notifications/
    AgUiDriftNotifier.cs
    AgUiLearningNotifier.cs
  Events/
    DriftWarnEvent.cs
    DriftAlertEvent.cs
    DriftEscalateEvent.cs
    DriftResolvedEvent.cs
    LearningCapturedEvent.cs
    LearningAppliedEvent.cs
    LearningForgottenEvent.cs
```

---

## Dependency Order

Sections should be implemented in order, as later sections depend on earlier ones:

1. Domain models (1-2) — no dependencies
2. Config (3-4) — depends on domain enums
3. Application interfaces (5-6) — depends on domain models
4. EWMA scorer (7) — depends on interfaces
5. Drift service (8) — depends on scorer + interfaces
6. Stores (9-10) — depends on interfaces
7. Decay service (11) — depends on interfaces + config
8. Learnings store (12) — depends on interfaces
9. MediatR handlers (13) — depends on interfaces + stores + decay
10. AG-UI events (14-15) — depends on domain models + interfaces
11. Escalation integration (16) — depends on drift service + Phase 2 interfaces
12. Learnings integration (17) — depends on drift service + learnings handlers
13. DI registration (18) — depends on all implementations
14. Config files (19) — depends on config classes
15. Full verification (20) — depends on everything

# Phase 3 Complete Specification: Quality Loop — Drift Detection + Learnings Log

## Overview

Phase 3 closes the feedback loop in the Microsoft Agentic Harness. Two interconnected subsystems detect when agent outputs degrade over time (Drift Detection) and capture corrections that improve future behavior (Learnings Log). They feed each other: drift detection identifies regressions, learnings log captures what worked and amplifies it, and high-confidence learnings adjust baselines to prevent false-positive drift alerts.

This builds on Phase 1 (Autonomy Tiers + Supervisor Agent) and Phase 2 (Human Escalation + Fallback Chains), both complete on `feat/agt-governance-integration`.

## Architecture Decisions

### From Interview

1. **Baseline granularity:** Full 3-level hierarchy — agent > skill > task-type
2. **Learning scope:** Hierarchical 3-tier — agent < team < global
3. **Drift storage:** Knowledge graph nodes (drift events as graph entities)
4. **Threshold strategy:** EWMA (exponentially weighted moving average), configurable lambda
5. **Decay model:** 3-category fixed decay — volatile (days), stable (months), permanent (never)
6. **Real-time events:** AG-UI SSE events (consistent with Phase 2 escalation patterns)
7. **Drift response:** Tiered — warn -> alert -> escalate (three severity bands)
8. **Learnings API:** MediatR commands (RememberCommand, RecallQuery, ForgetCommand, ImproveLearningCommand)

### From Research

1. **Drift detection approach:** EWMA charts for self-calibrating threshold detection, multi-dimensional scoring across faithfulness, relevance, structural conformance, tool usage accuracy
2. **Rolling baselines:** 7-day and 30-day sliding windows rather than fixed-point snapshots
3. **Bias-corrected EMA:** For new nodes with few feedback signals — `corrected = EMA / (1 - (1-alpha)^t)`
4. **Echo chamber prevention:** Diversity injection (10-20% non-feedback-optimized results) + feedback weight ceiling (30%)
5. **Provenance model:** W3C PROV-inspired correction records with temporal + semantic edges
6. **Memory types:** Typed learnings (Facts, Events, Instructions, Corrections, Preferences) with type-specific retention

---

## Subsystem 1: Drift Detection

### Domain Model

**`DriftBaseline`** — immutable record representing "known good" quality snapshot
- `BaselineId` (Guid)
- `Scope` (DriftScope: Agent, Skill, TaskType)
- `ScopeIdentifier` (string — agent ID, skill name, or task type key)
- `Dimensions` (IReadOnlyDictionary<DriftDimension, double> — per-dimension baseline values)
- `SampleCount` (int — how many evaluations this baseline was built from)
- `WindowStart` / `WindowEnd` (DateTimeOffset — time range of samples)
- `CreatedAt` (DateTimeOffset)

**`DriftDimension`** — enum
- Faithfulness, Relevance, StructuralConformance, ToolUsageAccuracy, Coherence, InstructionFollowing

**`DriftScore`** — immutable record for a single evaluation's drift measurement
- `ScoreId` (Guid)
- `BaselineId` (Guid — which baseline this was scored against)
- `Scope` / `ScopeIdentifier`
- `Dimensions` (IReadOnlyDictionary<DriftDimension, DriftDimensionScore>)
- `OverallDrift` (double — aggregate across dimensions)
- `Severity` (DriftSeverity: None, Warn, Alert, Escalate)
- `ScoredAt` (DateTimeOffset)

**`DriftDimensionScore`** — record
- `CurrentValue` (double)
- `BaselineValue` (double)
- `EwmaValue` (double — exponentially weighted moving average)
- `Deviation` (double — how far from baseline, in baseline sigma units)

**`DriftSeverity`** — enum with configurable thresholds
- `None` — within normal range
- `Warn` — deviation exceeds warning threshold (log + SSE event)
- `Alert` — deviation exceeds alert threshold (log + SSE event + dashboard highlight)
- `Escalate` — deviation exceeds escalation threshold (log + SSE + trigger Phase 2 escalation)

**`DriftEvent`** — knowledge graph entity representing a detected drift occurrence
- Node type: `"DriftEvent"`
- Properties: drift score, severity, affected scope, timestamp, resolution status
- Edges: `"affects"` -> agent/skill/task-type nodes, `"triggered_by"` -> evaluation run, `"resolved_by"` -> learning entry (if corrected)

### Interfaces

**`IDriftDetectionService`** (`Application.AI.Common/Interfaces/DriftDetection`)
- `EvaluateDriftAsync(DriftEvaluationRequest)` -> `Result<DriftScore>` — score a single evaluation against its baseline
- `GetBaselineAsync(DriftScope, scopeIdentifier)` -> `Result<DriftBaseline?>` — retrieve current baseline
- `UpdateBaselineAsync(DriftBaselineUpdateRequest)` -> `Result<DriftBaseline>` — recalculate baseline from recent evaluations
- `GetDriftHistoryAsync(DriftHistoryQuery)` -> `Result<IReadOnlyList<DriftScore>>` — time-series query

**`IDriftBaselineStore`** — persistence for baselines
- `SaveBaselineAsync(DriftBaseline)` -> `Result`
- `GetBaselineAsync(DriftScope, scopeIdentifier)` -> `Result<DriftBaseline?>`
- `GetBaselinesAsync(DriftScope)` -> `Result<IReadOnlyList<DriftBaseline>>`

**`IDriftScorer`** — keyed DI, pluggable scoring strategies
- `ScoreDimensionAsync(DriftDimension, currentOutput, baselineData)` -> `Result<double>`
- Keys: `"ewma"` (default), extensible for future statistical/ML scorers

**`IDriftAuditStore`** — durable compliance records (modeled after `IEscalationAuditStore`)
- `RecordDriftEventAsync(DriftAuditRecord)` -> `Result`
- `GetDriftEventsAsync(DriftAuditQuery)` -> `Result<IReadOnlyList<DriftAuditRecord>>`

### EWMA Implementation

The EWMA chart tracks quality metrics over time:
- `EWMA_t = lambda * x_t + (1 - lambda) * EWMA_{t-1}`
- `lambda` (smoothing factor): configurable, default 0.2
- Control limits: `UCL = mu_0 + L * sigma * sqrt(lambda / (2 - lambda))`, `LCL = mu_0 - L * sigma * sqrt(lambda / (2 - lambda))`
- `L` (control limit width): configurable, default 3.0
- Drift detected when EWMA crosses control limits

### Tiered Response

| Severity | Deviation | Response |
|---|---|---|
| Warn | 1-2 sigma | Log warning + SSE `DriftWarnEvent` |
| Alert | 2-3 sigma | Log alert + SSE `DriftAlertEvent` + dashboard highlight |
| Escalate | > 3 sigma | Log critical + SSE `DriftEscalateEvent` + `IEscalationService.QueueEscalationAsync()` |

Thresholds configurable per scope via `DriftConfig`.

### Baseline Management

- **Initial capture:** After N evaluations (configurable, default: 20), compute baseline from accumulated scores
- **Rolling update:** Baselines recalculated on configurable cadence (default: 7 days) using sliding window
- **Learning-adjusted:** When a learning entry with high confidence is applied, baseline adjusts to incorporate the correction (prevents false-positive drift on intentional changes)
- **3-level hierarchy:** agent baselines aggregate from skill baselines, skill from task-type. Missing levels fall back to parent.

---

## Subsystem 2: Learnings Log

### Domain Model

**`LearningEntry`** — immutable record representing a captured learning
- `LearningId` (Guid)
- `Category` (LearningCategory: FactualCorrection, StylePreference, ToolUsagePattern, DomainKnowledge, InstructionUpdate)
- `DecayClass` (DecayClass: Volatile, Stable, Permanent)
- `Scope` (LearningScope record: AgentId?, TeamId?, IsGlobal)
- `Content` (string — the learning itself)
- `Source` (LearningSource record: SourceType, SourceId, SourceDescription)
- `Provenance` (LearningProvenance record: OriginPipeline, OriginTask, OriginTimestamp, Confidence)
- `FeedbackWeight` (double — EMA-weighted quality score, default 1.0)
- `UpdateCount` (int)
- `CreatedAt` / `LastAccessedAt` / `LastReinforcedAt` (DateTimeOffset)

**`LearningCategory`** — enum
- `FactualCorrection` — human corrected a factual error (DecayClass: Permanent)
- `StylePreference` — style/format/tone preference (DecayClass: Stable)
- `ToolUsagePattern` — correct tool selection or parameter patterns (DecayClass: Stable)
- `DomainKnowledge` — domain-specific facts or rules (DecayClass: Stable)
- `InstructionUpdate` — procedure or instruction change (DecayClass: Stable)

**`DecayClass`** — enum with configurable shelf lives
- `Volatile` — shelf life in days (default: 7). Events, transient state.
- `Stable` — shelf life in days (default: 180). Preferences, patterns, domain knowledge.
- `Permanent` — never decays. Corrections, hard-won lessons.

**`LearningScope`** — record for hierarchical scoping
- `AgentId` (string?) — agent-specific
- `TeamId` (string?) — team-shared
- `IsGlobal` (bool) — available to all agents
- Resolution order: agent-specific > team > global. Higher-scoped learnings act as defaults; agent-scoped override.

**`LearningSource`** — record for provenance
- `SourceType` (LearningSourceType: HumanCorrection, DriftDetection, EscalationResolution, AgentSelfImprovement, ManualEntry)
- `SourceId` (string — escalation ID, drift event ID, etc.)
- `SourceDescription` (string)

### Interfaces

**MediatR Commands (Application layer):**
- `RememberCommand` -> `Result<LearningEntry>` — store a new learning
- `RecallQuery` -> `Result<IReadOnlyList<WeightedLearning>>` — retrieve relevant learnings for a context
- `ForgetCommand` -> `Result` — mark a learning as forgotten (soft delete with audit)
- `ImproveLearningCommand` -> `Result<LearningEntry>` — reinforce or update an existing learning with new feedback

**`WeightedLearning`** — record returned by Recall
- `Learning` (LearningEntry)
- `RelevanceScore` (double — semantic similarity to query)
- `FeedbackScore` (double — EMA-weighted quality)
- `FreshnessScore` (double — temporal freshness based on decay class)
- `FinalScore` (double — blended: `(1 - feedback_alpha) * relevance + feedback_alpha * feedback * freshness`)

**`ILearningsStore`** — persistence (keyed DI)
- `SaveLearningAsync(LearningEntry)` -> `Result`
- `GetLearningAsync(Guid learningId)` -> `Result<LearningEntry?>`
- `SearchLearningsAsync(LearningSearchCriteria)` -> `Result<IReadOnlyList<LearningEntry>>`
- `UpdateLearningAsync(LearningEntry)` -> `Result`
- `SoftDeleteLearningAsync(Guid learningId, string reason)` -> `Result`
- Keys: `"graph"` (knowledge graph backed), `"in_memory"` (testing)

**`ILearningDecayService`** — handles temporal decay
- `CalculateFreshnessAsync(LearningEntry)` -> `double` (0.0-1.0)
- `PruneExpiredLearningsAsync()` -> `Result<int>` (count of pruned)

### Recall Pipeline

1. **Query embedding** — embed the recall context
2. **Candidate retrieval** — semantic search across learnings store (filtered by scope hierarchy)
3. **Freshness scoring** — apply decay class shelf life
4. **Feedback weighting** — apply EMA-weighted quality score
5. **Blending** — `finalScore = (1 - alpha) * relevance + alpha * (feedback * freshness)`
6. **Diversity injection** — ensure 10-20% of results are non-feedback-optimized
7. **Feedback ceiling** — cap feedback influence at 30% of final score
8. **Return** — top-K results as `WeightedLearning` records

### Echo Chamber Prevention

- **Diversity injection:** 10-20% of recall results bypass feedback weighting
- **Feedback ceiling:** Feedback score can boost/demote by at most 30%
- **Periodic baseline evaluation:** Every N recalls, evaluate a subset on pure semantic relevance to detect feedback-induced divergence
- **Bias-corrected EMA:** `corrected = EMA / (1 - (1-alpha)^t)` for learnings with few feedback signals

---

## Cross-Cutting Concerns

### Drift -> Learnings Integration

- When drift is detected and subsequently corrected (via escalation or manual feedback), the correction is automatically captured as a `LearningEntry` with `SourceType.DriftDetection`
- High-confidence learnings (feedback weight > configurable threshold) trigger baseline adjustment in drift detection, preventing false positives on intentional changes
- Drift events stored as knowledge graph nodes with edges to the learning entries that resolved them

### AG-UI SSE Events

New event types (modeled after escalation events):
- `DriftWarnEvent` — drift severity: warn. Fields: scope, dimensions, deviation.
- `DriftAlertEvent` — drift severity: alert. Same fields + affected baseline ID.
- `DriftEscalateEvent` — drift severity: escalate. Same fields + escalation ID.
- `LearningCapturedEvent` — new learning stored. Fields: learning ID, category, scope, source.
- `LearningAppliedEvent` — learning used in recall. Fields: learning ID, agent ID, context.

Implementation: `AgUiDriftNotifier` and `AgUiLearningNotifier` implementing `IDriftNotificationChannel` and `ILearningNotificationChannel` respectively. Same graceful no-op pattern as `AgUiEscalationNotifier`.

### Configuration

**`DriftConfig`** (new section under `AppConfig.AI`):
- `Enabled` (bool, default: true)
- `EwmaLambda` (double, default: 0.2 — smoothing factor)
- `ControlLimitWidth` (double, default: 3.0 — sigma multiplier for control limits)
- `MinSamplesForBaseline` (int, default: 20)
- `BaselineWindowDays` (int, default: 7)
- `WarnThresholdSigma` (double, default: 1.5)
- `AlertThresholdSigma` (double, default: 2.5)
- `EscalateThresholdSigma` (double, default: 3.0)
- `EscalationEnabled` (bool, default: true — whether to trigger Phase 2 escalation)

**`LearningsConfig`** (new section under `AppConfig.AI`):
- `Enabled` (bool, default: true)
- `FeedbackAlpha` (double, default: 0.25 — feedback weight in blending)
- `FeedbackCeiling` (double, default: 0.3 — max feedback influence)
- `DiversityInjectionRatio` (double, default: 0.15)
- `VolatileShelfLifeDays` (int, default: 7)
- `StableShelfLifeDays` (int, default: 180)
- `PruneIntervalHours` (int, default: 24)
- `BaselineAdjustmentThreshold` (double, default: 0.8 — min feedback weight to trigger baseline adjustment)
- `BiasCorrection` (bool, default: true)

### Observability

- OpenTelemetry metrics: `drift.evaluations.total`, `drift.severity.{level}`, `learnings.remembered.total`, `learnings.recalled.total`, `learnings.pruned.total`
- OpenTelemetry traces: span per drift evaluation, span per recall pipeline
- Structured logging at each severity level

### Testing Strategy

- 80%+ coverage, TDD approach
- Unit tests: EWMA calculation, decay freshness, feedback blending, diversity injection, scope resolution
- Integration tests: full drift evaluation pipeline, recall pipeline with in-memory stores
- Domain tests: baseline management, learning scope hierarchy, severity classification
- Test helpers: `FakeTimeProvider` for deterministic time, in-memory graph store, test learning fixtures

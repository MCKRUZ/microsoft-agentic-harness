# Section 7: EWMA Drift Scorer Implementation

## Overview

This section implements `EwmaDriftScorer`, the core drift scoring engine that uses Exponentially Weighted Moving Average (EWMA) to detect quality regressions. It also includes a pure severity classifier function and an `IEwmaStateStore` implementation backed by the knowledge graph.

**Layer:** Infrastructure.AI
**Depends on:** Section 03 (DriftDetectionConfig), Section 05 (IDriftScorer, IEwmaStateStore, EwmaState)
**Blocks:** Section 08 (DefaultDriftDetectionService)

---

## Background

### EWMA Primer

- **Formula:** `EWMA_t = lambda * x_t + (1 - lambda) * EWMA_{t-1}`
- **Control limits:**
  - `UCL = baseline_mean + L * sigma * sqrt(lambda / (2 - lambda))`
  - `LCL = baseline_mean - L * sigma * sqrt(lambda / (2 - lambda))`
  - `L` = control limit width, `sigma` = baseline standard deviation
- **Deviation** (sigma units): `abs(ewma - baseline_mean) / sigma`

---

## Dependencies from Prior Sections

**From Section 01:** `DriftDimension`, `DriftSeverity`, `DriftScope`, `DriftDimensionScore`, `DriftBaseline`
**From Section 03:** `DriftDetectionConfig` (EwmaLambda, ControlLimitWidth, thresholds, Enabled)
**From Section 05:** `IDriftScorer`, `IEwmaStateStore`, `EwmaState`
**Existing:** `IKnowledgeGraphStore`, `GraphNode`, `Result<T>`, `AppConfig`

---

## Files to Create

| File | Action |
|------|--------|
| `src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/EwmaDriftScorerTests.cs` | Create |
| `src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/DriftSeverityClassifierTests.cs` | Create |
| `src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/GraphEwmaStateStoreTests.cs` | Create |
| `src/Content/Infrastructure/Infrastructure.AI/DriftDetection/EwmaDriftScorer.cs` | Create |
| `src/Content/Infrastructure/Infrastructure.AI/DriftDetection/DriftSeverityClassifier.cs` | Create |
| `src/Content/Infrastructure/Infrastructure.AI/DriftDetection/GraphEwmaStateStore.cs` | Create |

---

## Tests (Write First)

### EwmaDriftScorerTests.cs

```csharp
// Setup: Mock<IEwmaStateStore>, Mock<IOptionsMonitor<AppConfig>>, FakeTimeProvider, EwmaDriftScorer

// Test: ScoreDimension_FirstEvaluation_InitializesFromBaselineMean
//   State store returns null, baseline mean=0.8, sigma=0.1
//   EWMA = 0.2 * 0.75 + 0.8 * 0.8 = 0.79, SampleCount=1

// Test: ScoreDimension_SubsequentEvaluation_AppliesEwmaFormula
//   Existing state CurrentEwma=0.79, SampleCount=1
//   EWMA = 0.2 * 0.7 + 0.8 * 0.79 = 0.772, SampleCount=2

// Test: ScoreDimension_KnownInputs_ProducesExpectedEwma
//   5 observations [0.8, 0.75, 0.7, 0.65, 0.6], verify final EWMA to 6 decimal places

// Test: ScoreDimension_LambdaZeroPointTwo_WeightsHistoryEighty
//   Prior EWMA=0.8, currentValue=0.0 -> new EWMA=0.64

// Test: ScoreDimension_DeviationCalculation_CorrectSigmaUnits
//   Baseline mean=0.8, sigma=0.1, EWMA=0.55 -> Deviation=2.5

// Test: ScoreDimension_ZeroVariance_ReturnsZeroDeviation
//   Baseline sigma=0 -> Deviation=0.0, no division by zero

// Test: ScoreDimension_SavesUpdatedEwmaState
// Test: ScoreDimension_LoadsExistingEwmaState
// Test: ScoreDimension_UsesTimeProviderForTimestamp
// Test: ScoreDimension_DisabledConfig_ReturnsSuccessNoOp
```

### DriftSeverityClassifierTests.cs

```csharp
// Default thresholds: Warn=1.5, Alert=2.5, Escalate=3.0

// Test: SeverityClassifier_BelowWarn_ReturnsNone (deviation=1.0)
// Test: SeverityClassifier_BetweenWarnAndAlert_ReturnsWarn (deviation=2.0)
// Test: SeverityClassifier_BetweenAlertAndEscalate_ReturnsAlert (deviation=2.8)
// Test: SeverityClassifier_AboveEscalate_ReturnsEscalate (deviation=3.5)
// Test: SeverityClassifier_ExactlyAtThreshold_ReturnsHigherSeverity
//   1.5 -> Warn, 2.5 -> Alert, 3.0 -> Escalate
```

### GraphEwmaStateStoreTests.cs

```csharp
// Setup: Mock<IKnowledgeGraphStore>

// Test: GetState_ExistingNode_DeserializesEwmaState
// Test: GetState_NoNode_ReturnsNull
// Test: SaveState_CreatesGraphNodeWithDeterministicId
//   ID = "ewma:Skill:code_review:Faithfulness"
// Test: SaveState_OverwritesExistingNode
// Test: GetStates_ReturnsAllDimensionsForScope
```

---

## Implementation Details

### EwmaDriftScorer

**Constructor:** `IEwmaStateStore`, `IOptionsMonitor<AppConfig>`, `TimeProvider`, `ILogger<EwmaDriftScorer>`
**Keyed DI:** `"ewma"`

**ScoreDimensionAsync flow:**

1. Check `config.DriftDetection.Enabled` -- if false, return success no-op with zero deviation
2. Get baseline mean for dimension from `baseline.Dimensions[dimension]`
3. Get baseline sigma from `baseline.DimensionSigmas[dimension]`
4. Load existing EWMA state (null = first evaluation)
5. Previous EWMA = state exists ? `state.CurrentEwma` : `baselineMean`
6. Compute: `newEwma = lambda * currentValue + (1 - lambda) * previousEwma`
7. Compute: `deviation = sigma > 0 ? Math.Abs(newEwma - baselineMean) / sigma : 0.0`
8. Save updated `EwmaState` with incremented SampleCount, TimeProvider timestamp
9. Return `DriftDimensionScore` with CurrentValue, BaselineValue, EwmaValue, Deviation

### DriftSeverityClassifier

Static class, pure function:

```csharp
public static DriftSeverity Classify(double deviation, DriftDetectionConfig config)
```

Logic: Check from highest to lowest. `>=` means at-threshold yields higher severity.

```
if deviation >= EscalateThresholdSigma -> Escalate
else if >= AlertThresholdSigma -> Alert
else if >= WarnThresholdSigma -> Warn
else -> None
```

### GraphEwmaStateStore

**Constructor:** `IKnowledgeGraphStore`, `ILogger<GraphEwmaStateStore>`
**Deterministic ID:** `"ewma:{scope}:{identifier}:{dimension}"`

**GetStateAsync:** Build ID -> `graphStore.GetNodeAsync(id)` -> deserialize from `GraphNode.Properties`
**SaveStateAsync:** Build ID -> create `GraphNode` with Type="EwmaState", Properties dict -> `graphStore.AddNodesAsync`
**GetStatesAsync:** Iterate all `DriftDimension` enum values, build ID, call `GetNodeAsync`, filter nulls

---

## Edge Cases

1. **Zero variance (sigma = 0):** Returns `deviation = 0.0`. Zero-variance dimensions cannot trigger drift. Logged at debug level.
2. **Missing dimension in baseline:** Returns `Result.Fail`. Caller should only request existing dimensions.
3. **First evaluation (no prior state):** EWMA initialized from baseline mean, not currentValue. Prevents false alarms from a single observation.
4. **Config hot-reload:** `IOptionsMonitor` reads current values each call. EWMA state not invalidated on config change.
5. **Thread safety:** Registered as Singleton, holds no mutable state. Graph store upsert provides eventual consistency for concurrent calls.

## DI Registration (Deferred to Section 18)

```
IDriftScorer [keyed "ewma"] -> EwmaDriftScorer (Singleton)
IEwmaStateStore -> GraphEwmaStateStore (Singleton)
```

---

## Implementation Notes (Post-Build)

### Deviations from Plan
- **GetStatesAsync parallelized:** Changed from sequential foreach to `Task.WhenAll` for all dimension lookups (review fix).
- **ScopeIdentifier colon guard:** Added `ArgumentException` in `BuildId()` to prevent ambiguous deterministic IDs (review fix).
- **2 additional tests added:** `ScoreDimension_GetStateFails_PropagatesError` and `ScoreDimension_SaveStateFails_PropagatesError` for error propagation coverage.

### Final Test Count
- 24 tests (10 EwmaDriftScorer + 7 DriftSeverityClassifier + 5 GraphEwmaStateStore + 2 error propagation)

### Files Created (Actual)
All 6 files created as planned — no path deviations.

# Section 8: Drift Detection Service Implementation

## Overview

This section implements `DefaultDriftDetectionService`, the orchestrator for the full drift evaluation pipeline, plus `CompositeDriftNotifier` for fan-out notifications and `DriftMetrics` / `DriftConventions` for OpenTelemetry observability.

**Layer:** Infrastructure.AI (service + notifier), Application.AI.Common (metrics), Domain.AI (conventions)
**Depends on:** Section 07 (EwmaDriftScorer, DriftSeverityClassifier), Section 05 (interfaces + DTOs), Section 03 (DriftDetectionConfig), Section 01 (domain models)
**Blocks:** Section 16 (DriftEscalationBridge), Section 17 (LearningsBridge)

---

## Background

`DefaultDriftDetectionService` implements `IDriftDetectionService` and orchestrates:

1. **Baseline resolution** with scope fallback hierarchy (TaskType -> Skill -> Agent)
2. **Per-dimension scoring** via `IDriftScorer` (keyed `"ewma"`, implemented in Section 7)
3. **Severity classification** via `DriftSeverityClassifier` (Section 7)
4. **Escalation trigger** via `IEscalationService` (Phase 2) when severity reaches `Escalate`
5. **Audit recording** via `IDriftAuditStore`
6. **Notification dispatch** via `IDriftNotifier` (composite pattern, fans out to all `IDriftNotificationChannel` implementations)

`CompositeDriftNotifier` mirrors `CompositeEscalationNotifier` exactly: it takes `IEnumerable<IDriftNotificationChannel>` via DI, fans out calls to all channels, and catches/logs individual channel failures without blocking other channels.

`DriftMetrics` is a static class of OTel instruments following the `EscalationMetrics` pattern, using `DriftConventions` for semantic naming.

---

## Dependencies from Prior Sections

**Section 01 (domain):** `DriftDimension`, `DriftSeverity`, `DriftScope`, `DriftDimensionScore`, `DriftBaseline`, `DriftScore`, `DriftEvent`, `DriftResolution`, `DriftResolutionType`, `DriftAuditRecord`, `DriftAuditRecordType`

**Section 03 (config):** `DriftDetectionConfig` with properties: `Enabled`, `EwmaLambda`, `ControlLimitWidth`, `MinSamplesForBaseline`, `BaselineWindowDays`, `WarnThresholdSigma`, `AlertThresholdSigma`, `EscalateThresholdSigma`, `EscalationEnabled`, `AuditPath`

**Section 05 (interfaces):** `IDriftDetectionService`, `IDriftBaselineStore`, `IDriftScorer`, `IDriftAuditStore`, `IDriftNotificationChannel`, `IDriftNotifier`, `DriftEvaluationRequest`, `DriftBaselineUpdateRequest`, `DriftHistoryQuery`, `EwmaState`

**Section 07 (EWMA scorer):** `EwmaDriftScorer` (implements `IDriftScorer`), `DriftSeverityClassifier` (static, `Classify(double deviation, DriftDetectionConfig config) -> DriftSeverity`)

**Existing codebase:**
- `IEscalationService` at `src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationService.cs` — `QueueEscalationAsync(EscalationRequest, CancellationToken) -> Task<Guid>`
- `EscalationRequest` at `src/Content/Domain/Domain.AI/Escalation/` — includes `AgentId`, `ToolName`, `Description`, `RiskLevel`, `ApprovalStrategy`, `Approvers`
- `IKnowledgeGraphStore` for drift event graph node persistence
- `Result` / `Result<T>` from `Domain.Common` — `Result.Success()`, `Result.Fail("message")`, `Result<T>.Success(value)`, `Result<T>.Fail("message")`
- `AppInstrument.Meter` from `Domain.Common.Telemetry` for OTel metrics
- `EscalationMetrics` and `EscalationConventions` patterns to follow
- `CompositeEscalationNotifier` pattern to follow for `CompositeDriftNotifier`
- `TimeProvider` injection (not `DateTimeOffset.UtcNow`) for testability

---

## Files to Create

| File | Project | Purpose |
|------|---------|---------|
| `src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/DefaultDriftDetectionServiceTests.cs` | Infrastructure.AI.Tests | Tests for drift service |
| `src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/CompositeDriftNotifierTests.cs` | Infrastructure.AI.Tests | Tests for composite notifier |
| `src/Content/Tests/Application.AI.Common.Tests/OpenTelemetry/Metrics/DriftMetricsTests.cs` | Application.AI.Common.Tests | Tests for OTel metrics |
| `src/Content/Infrastructure/Infrastructure.AI/DriftDetection/DefaultDriftDetectionService.cs` | Infrastructure.AI | Service implementation |
| `src/Content/Infrastructure/Infrastructure.AI/DriftDetection/CompositeDriftNotifier.cs` | Infrastructure.AI | Composite notifier |
| `src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/DriftMetrics.cs` | Application.AI.Common | OTel metric instruments |
| `src/Content/Domain/Domain.AI/Telemetry/Conventions/DriftConventions.cs` | Domain.AI | OTel semantic conventions |

---

## Tests (Write First)

### DefaultDriftDetectionServiceTests.cs

Path: `src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/DefaultDriftDetectionServiceTests.cs`

Test class setup mirrors `DefaultEscalationServiceTests`: constructor wires mocks, helper methods create test fixtures.

**Constructor dependencies (all mocked):**
- `IDriftScorer` (keyed `"ewma"`)
- `IDriftBaselineStore`
- `IDriftAuditStore`
- `IDriftNotifier`
- `IEscalationService`
- `IKnowledgeGraphStore`
- `IOptionsMonitor<AppConfig>` (returns `DriftDetectionConfig` with defaults)
- `TimeProvider` (`FakeTimeProvider`)
- `ILogger<DefaultDriftDetectionService>`

**Helper methods:**
- `CreateTestBaseline(DriftScope, string scopeIdentifier, ...)` — returns a `DriftBaseline` with known dimension means and sigmas
- `CreateTestEvaluationRequest(DriftScope, string scopeIdentifier, Dictionary<DriftDimension, double> dimensions)` — returns a `DriftEvaluationRequest`
- `SetupBaselineReturn(DriftScope, string, DriftBaseline?)` — configures mock baseline store to return specific baselines for scope lookups
- `SetupScorerReturns(DriftDimensionScore)` — configures mock scorer to return a specific score for any dimension

```csharp
namespace Infrastructure.AI.Tests.DriftDetection;

public sealed class DefaultDriftDetectionServiceTests
{
    // Setup: Mock<IDriftScorer>, Mock<IDriftBaselineStore>, Mock<IDriftAuditStore>,
    //   Mock<IDriftNotifier>, Mock<IEscalationService>, Mock<IKnowledgeGraphStore>,
    //   IOptionsMonitor<AppConfig> with DriftDetectionConfig defaults,
    //   FakeTimeProvider, DefaultDriftDetectionService _sut

    // ===== EvaluateDriftAsync =====

    // Test: EvaluateDrift_WithBaseline_ScoresAllDimensions
    //   Baseline exists with 3 dimensions, request has 3 dimension values.
    //   Scorer mock returns DriftDimensionScore for each.
    //   Verify scorer called 3 times, result contains 3 dimension scores.

    // Test: EvaluateDrift_NoBaseline_ReturnsFailure
    //   Baseline store returns null for all scope levels.
    //   Result.IsSuccess == false, failure message contains "No baseline available".

    // Test: EvaluateDrift_BaselineFallback_TaskTypeToSkillToAgent
    //   Baseline store: TaskType returns null, Skill returns null, Agent returns baseline.
    //   Verify baseline store called 3 times in fallback order.
    //   Result uses the Agent-scope baseline.

    // Test: EvaluateDrift_SeverityWarn_EmitsNotification
    //   Scorer returns deviation=2.0 (between Warn=1.5 and Alert=2.5).
    //   Notifier.NotifyDriftDetectedAsync called once.
    //   EscalationService NOT called.

    // Test: EvaluateDrift_SeverityAlert_EmitsNotification
    //   Scorer returns deviation=2.8 (between Alert=2.5 and Escalate=3.0).
    //   Notifier.NotifyDriftDetectedAsync called once.
    //   EscalationService NOT called.

    // Test: EvaluateDrift_SeverityEscalate_TriggersEscalationService
    //   Scorer returns deviation=3.5 (above Escalate=3.0).
    //   EscalationService.QueueEscalationAsync called once.
    //   EscalationRequest.ToolName == "drift_detection".
    //   EscalationRequest.RiskLevel == RiskLevel.High.
    //   Notifier.NotifyDriftDetectedAsync also called.

    // Test: EvaluateDrift_SeverityEscalate_EscalationDisabled_SkipsEscalation
    //   Config.EscalationEnabled = false. Scorer returns deviation=3.5.
    //   EscalationService.QueueEscalationAsync NOT called.
    //   Notifier.NotifyDriftDetectedAsync still called.

    // Test: EvaluateDrift_RecordsAuditEntry
    //   Any evaluation result -> AuditStore.RecordAsync called once.
    //   DriftAuditRecord.RecordType == DriftAuditRecordType.Detected when severity >= Warn.

    // Test: EvaluateDrift_CreatesGraphNodeForDriftEvent
    //   Severity >= Warn -> KnowledgeGraphStore.AddNodesAsync called.
    //   Graph node type == "DriftEvent", properties contain serialized DriftScore.

    // Test: EvaluateDrift_OverallDrift_IsMaxDeviation
    //   3 dimensions with deviations [1.0, 2.5, 1.8].
    //   DriftScore.OverallDrift == 2.5 (max).

    // Test: EvaluateDrift_Disabled_ReturnsSuccessNoOp
    //   Config.Enabled = false.
    //   Returns Result<DriftScore>.Success with DriftSeverity.None.
    //   No calls to scorer, baseline store, audit, or notifier.

    // ===== UpdateBaselineAsync =====

    // Test: UpdateBaseline_ComputesMeanAndVariance
    //   History returns 5 scores per dimension with known values.
    //   New baseline's Dimensions contains correct mean.
    //   New baseline's DimensionSigmas contains correct standard deviation.

    // Test: UpdateBaseline_RollingWindowFiltersOldScores
    //   Config.BaselineWindowDays = 7. FakeTimeProvider set to known "now".
    //   History has scores at day-3 and day-10. Only day-3 included.

    // Test: UpdateBaseline_InsufficientSamples_ReturnsFailure
    //   History returns 5 scores, Config.MinSamplesForBaseline = 20.
    //   Result.IsSuccess == false. Baseline NOT saved.

    // Test: UpdateBaseline_RecordsAuditEntry
    //   Successful update -> AuditStore.RecordAsync called.
    //   RecordType == DriftAuditRecordType.BaselineUpdated.

    // ===== GetDriftHistoryAsync =====

    // Test: GetDriftHistory_ReturnsScoresInDateRange
    //   Stub: returns scores from internal in-memory list.
    //   (Implementation detail: drift scores stored as graph nodes, queried by date range.)

    // ===== Metrics =====

    // Test: DriftMetrics_EvaluationCounterIncrements
    //   Call EvaluateDriftAsync, verify DriftMetrics.Evaluations counter incremented.
    //   (Use MeterListener or verify via OTel test exporter.)

    // Test: DriftMetrics_EscalationCounterIncrements
    //   Trigger escalation-severity evaluation.
    //   Verify DriftMetrics.EscalationsTriggered counter incremented.
}
```

### CompositeDriftNotifierTests.cs

Path: `src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/CompositeDriftNotifierTests.cs`

Mirrors `CompositeEscalationNotifierTests` exactly.

```csharp
namespace Infrastructure.AI.Tests.DriftDetection;

public sealed class CompositeDriftNotifierTests
{
    // Setup: multiple Mock<IDriftNotificationChannel>, CompositeDriftNotifier _sut

    // Test: NotifyDriftDetected_FansOutToAllChannels
    //   3 channels registered. Call NotifyDriftDetectedAsync.
    //   All 3 channels receive the call.

    // Test: NotifyDriftResolved_FansOutToAllChannels
    //   Same pattern for NotifyDriftResolvedAsync.

    // Test: ChannelFailure_LogsWarning_DoesNotBlockOtherChannels
    //   Channel 1 throws, Channel 2 succeeds.
    //   Channel 2 still receives the call. Exception logged.

    // Test: NoChannels_CompletesWithoutError
    //   0 channels registered. Calls succeed (no-op).
}
```

### DriftMetricsTests.cs

Path: `src/Content/Tests/Application.AI.Common.Tests/OpenTelemetry/Metrics/DriftMetricsTests.cs`

```csharp
namespace Application.AI.Common.Tests.OpenTelemetry.Metrics;

public sealed class DriftMetricsTests
{
    // Test: Evaluations_Counter_IsNotNull
    // Test: EscalationsTriggered_Counter_IsNotNull
    // Test: BaselinesUpdated_Counter_IsNotNull
    // Test: EvaluationDurationMs_Histogram_IsNotNull
    //   Simple non-null checks prove instruments registered correctly.
    //   Actual recording verified in DefaultDriftDetectionServiceTests.
}
```

---

## Implementation Details

### DefaultDriftDetectionService

Path: `src/Content/Infrastructure/Infrastructure.AI/DriftDetection/DefaultDriftDetectionService.cs`

**Constructor dependencies:**
- `IDriftScorer` — resolved via keyed DI `"ewma"` (use `[FromKeyedServices("ewma")]` attribute or `IServiceProvider` resolve)
- `IDriftBaselineStore` — resolved as default registration
- `IDriftAuditStore`
- `IDriftNotifier` — composite, fans out to all channels
- `IEscalationService` — Phase 2, for triggering escalation on severe drift
- `IKnowledgeGraphStore` — for persisting drift events as graph nodes
- `IOptionsMonitor<AppConfig>` — config access via `CurrentValue.AI.DriftDetection`
- `TimeProvider` — for testable timestamps
- `ILogger<DefaultDriftDetectionService>`

**Enabled guard pattern:** All public methods start with:
```csharp
var config = _config.CurrentValue.AI.DriftDetection;
if (!config.Enabled)
    return Result<DriftScore>.Success(/* no-op score with Severity=None */);
```

This matches the convention described in the plan: "Services check `Enabled` config flag with early-return no-op when disabled."

**`EvaluateDriftAsync` flow:**

1. Read `DriftDetectionConfig` from `IOptionsMonitor`
2. Resolve baseline via fallback hierarchy:
   - If request scope is `TaskType`: try TaskType -> Skill -> Agent
   - If request scope is `Skill`: try Skill -> Agent
   - If request scope is `Agent`: try Agent only
   - Call `IDriftBaselineStore.GetBaselineAsync(scope, identifier)` at each level
   - If no baseline found at any level: `return Result<DriftScore>.Fail("No baseline available for scope {scope}:{identifier}")`
3. For each `(dimension, currentValue)` in `request.Dimensions`:
   - Call `IDriftScorer.ScoreDimensionAsync(dimension, currentValue, baseline, ct)`
   - Collect results into `Dictionary<DriftDimension, DriftDimensionScore>`
4. Compute `overallDrift = dimensions.Values.Max(d => d.Deviation)`
5. Classify severity: `DriftSeverityClassifier.Classify(overallDrift, config)`
6. Build `DriftScore` record with all computed values, `TimeProvider.GetUtcNow()` for `ScoredAt`
7. If severity >= `Warn`:
   - Create `DriftEvent` with new `Guid`, the score, no resolution, `DetectedAt` = now
   - Persist as graph node: type `"DriftEvent"`, deterministic ID `"driftevent:{eventId}"`, properties include serialized score + severity + scope
   - Add edge `"affects"` from drift event node to scope identifier node
8. If severity == `Escalate` and `config.EscalationEnabled`:
   - Build `EscalationRequest` with:
     - `AgentId` = scope identifier (or extract from scope context)
     - `ToolName` = `"drift_detection"` (convention for Section 16 to filter on)
     - `Description` = summary of drifted dimensions and their deviations
     - `RiskLevel` = `RiskLevel.High`
     - `ApprovalStrategy` = `ApprovalStrategyType.AnyOf` (or from config)
     - `Approvers` = empty list (all approvers)
   - Call `IEscalationService.QueueEscalationAsync(escalationRequest, ct)`
   - Store returned escalation ID on the drift event (update graph node)
   - Increment `DriftMetrics.EscalationsTriggered`
9. Record audit: `IDriftAuditStore.RecordAsync(new DriftAuditRecord { RecordType = Detected, ... })`
10. Notify: `IDriftNotifier.NotifyDriftDetectedAsync(score, ct)`
11. Increment `DriftMetrics.Evaluations` with tags: scope, severity
12. Record duration: `DriftMetrics.EvaluationDurationMs`
13. Return `Result<DriftScore>.Success(score)`

**`UpdateBaselineAsync` flow:**

1. Read config, check enabled
2. Build `DriftHistoryQuery` for the scope with date range: `[now - BaselineWindowDays, now]`
3. Call internal history retrieval (graph query for DriftScore nodes in date range)
4. If `count < config.MinSamplesForBaseline`: return `Result<DriftBaseline>.Fail("Insufficient samples: {count}/{required}")`
5. For each `DriftDimension` present in the history scores:
   - Compute mean and standard deviation across all scores for that dimension
6. Build new `DriftBaseline` record with computed means, sigmas, sample count, window range
7. Save via `IDriftBaselineStore.SaveBaselineAsync`
8. Record audit with `DriftAuditRecordType.BaselineUpdated`
9. Increment `DriftMetrics.BaselinesUpdated`
10. Return `Result<DriftBaseline>.Success(baseline)`

**`GetBaselineAsync`:** Delegates directly to `IDriftBaselineStore.GetBaselineAsync`.

**`GetDriftHistoryAsync`:** Queries graph for `DriftScore` nodes within the date range for the given scope. Deserializes and returns.

**Baseline fallback hierarchy helper:**
```csharp
/// <summary>
/// Walks the scope hierarchy from specific to general until a baseline is found.
/// TaskType -> Skill -> Agent.
/// </summary>
private async Task<DriftBaseline?> ResolveBaselineWithFallbackAsync(
    DriftScope scope, string scopeIdentifier, CancellationToken ct)
```

This is a private method that implements the loop over scopes. The `DriftScope` enum values (`TaskType=2`, `Skill=1`, `Agent=0`) should be ordered such that a simple decrement walks the fallback. If not, use explicit ordering: `[DriftScope.TaskType, DriftScope.Skill, DriftScope.Agent]` starting from the requested scope.

### CompositeDriftNotifier

Path: `src/Content/Infrastructure/Infrastructure.AI/DriftDetection/CompositeDriftNotifier.cs`

Exact mirror of `CompositeEscalationNotifier` at `src/Content/Infrastructure/Infrastructure.AI/Escalation/CompositeEscalationNotifier.cs`.

**Constructor:** `IEnumerable<IDriftNotificationChannel> channels`, `ILogger<CompositeDriftNotifier>`

**Methods:**
- `NotifyDriftDetectedAsync(DriftScore, CancellationToken)` — fans out via `FanOutAsync`
- `NotifyDriftResolvedAsync(DriftEvent, CancellationToken)` — fans out via `FanOutAsync`

**FanOutAsync pattern:**
```csharp
private Task FanOutAsync(Func<IDriftNotificationChannel, Task> action)
{
    var tasks = _channels.Select(channel => SafeNotifyAsync(action, channel));
    return Task.WhenAll(tasks);
}

private async Task SafeNotifyAsync(
    Func<IDriftNotificationChannel, Task> action,
    IDriftNotificationChannel channel)
{
    try { await action(channel); }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Drift notification channel {Channel} failed", channel.GetType().Name);
    }
}
```

Registered as `IDriftNotifier` singleton. Discovers all `IDriftNotificationChannel` via DI enumerable injection.

### DriftConventions

Path: `src/Content/Domain/Domain.AI/Telemetry/Conventions/DriftConventions.cs`

Follows `EscalationConventions` pattern exactly. Static class with:

**Attribute name constants:**
- `Scope` = `"agent.drift.scope"`
- `ScopeIdentifier` = `"agent.drift.scope_identifier"`
- `Severity` = `"agent.drift.severity"`
- `Dimension` = `"agent.drift.dimension"`

**Metric identifier constants:**
- `Evaluations` = `"agent.drift.evaluations"`
- `EscalationsTriggered` = `"agent.drift.escalations_triggered"`
- `BaselinesUpdated` = `"agent.drift.baselines_updated"`
- `EvaluationDurationMs` = `"agent.drift.evaluation_duration_ms"`

**Well-known tag value classes:**
- `SeverityValues`: None, Warn, Alert, Escalate (lowercase strings)
- `ScopeValues`: Agent, Skill, TaskType (lowercase strings)

### DriftMetrics

Path: `src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/DriftMetrics.cs`

Follows `EscalationMetrics` pattern:

```csharp
public static class DriftMetrics
{
    /// <summary>Drift evaluations completed. Tags: scope, severity.</summary>
    public static Counter<long> Evaluations { get; } =
        AppInstrument.Meter.CreateCounter<long>(DriftConventions.Evaluations, "{evaluation}", "Drift evaluations completed");

    /// <summary>Drift-triggered escalations.</summary>
    public static Counter<long> EscalationsTriggered { get; } =
        AppInstrument.Meter.CreateCounter<long>(DriftConventions.EscalationsTriggered, "{escalation}", "Drift-triggered escalations");

    /// <summary>Drift baselines updated.</summary>
    public static Counter<long> BaselinesUpdated { get; } =
        AppInstrument.Meter.CreateCounter<long>(DriftConventions.BaselinesUpdated, "{baseline}", "Drift baselines updated");

    /// <summary>Drift evaluation duration in milliseconds.</summary>
    public static Histogram<double> EvaluationDurationMs { get; } =
        AppInstrument.Meter.CreateHistogram<double>(DriftConventions.EvaluationDurationMs, "ms", "Drift evaluation duration");
}
```

---

## Graph Integration Details

Drift events are stored as knowledge graph nodes for correlation and querying:

- **Node ID:** `"driftevent:{eventId}"` — deterministic, enables direct lookup
- **Node type:** `"DriftEvent"`
- **Properties:** Serialized JSON containing: `DriftScore` (full), `Severity`, `Scope`, `ScopeIdentifier`, `DetectedAt`
- **Edges:**
  - `"affects"` -> scope identifier node (e.g., the agent or skill node)
  - `"resolved_by"` -> learning entry node (added later when drift is resolved, Section 16/17)
  - `"triggered_escalation"` -> escalation node (when escalation is triggered)

Graph node creation uses `IKnowledgeGraphStore.AddNodesAsync` with a single `GraphNode`. Edge creation uses `IKnowledgeGraphStore.AddEdgesAsync`. Follow the pattern used by `GraphFeedbackStore` and `GraphEwmaStateStore` for serialization into `Properties` dictionaries.

---

## Error Handling

- **Scorer failure:** If any `IDriftScorer.ScoreDimensionAsync` returns `Result.Fail`, skip that dimension and log a warning. The overall score uses only successful dimensions. If zero dimensions succeed, return failure.
- **Audit/notification failures:** Wrap in try/catch (same as `DefaultEscalationService.SafeExecuteAsync`). Audit and notification failures do not fail the evaluation.
- **Escalation service failure:** Wrap in try/catch. Log error. Drift event still created and notified.
- **Graph persistence failure:** Wrap in try/catch. Log error. The `Result<DriftScore>` still returns success since the score was computed correctly.

---

## Edge Cases

1. **Disabled config:** Early return with no-op score. No side effects.
2. **Baseline fallback exhausted:** Explicit failure result.
3. **Single dimension request:** `OverallDrift` equals that dimension's deviation.
4. **All dimensions below threshold:** `Severity = None`, no DriftEvent created, no notification, just audit + metrics.
5. **Scorer returns zero deviation for all:** `Severity = None`, same as above.
6. **Concurrent evaluations:** Service is singleton but stateless. All mutable state delegated to stores.

---

## DI Registration (Deferred to Section 18)

```
IDriftDetectionService -> DefaultDriftDetectionService (Singleton)
IDriftNotifier -> CompositeDriftNotifier (Singleton)
```

`CompositeDriftNotifier` auto-discovers all `IDriftNotificationChannel` implementations via `IEnumerable<IDriftNotificationChannel>`.

---

## Key Design Decisions

1. **Escalation via `QueueEscalationAsync` (non-blocking):** Drift evaluation should not block waiting for human approval. The escalation is queued; resolution flows back through `DriftEscalationBridge` (Section 16).

2. **`ToolName = "drift_detection"` convention:** Section 16's `DriftEscalationBridge` filters escalation resolutions by this tool name to identify drift-originated escalations. This is a string convention, not a typed reference.

3. **Graph node persistence for drift events:** Enables cross-system correlation (drift -> escalation -> learning) via graph edges. Follows the same pattern as `GraphFeedbackStore`.

4. **Metrics in Application layer, conventions in Domain:** `DriftMetrics` (instruments) lives in `Application.AI.Common/OpenTelemetry/Metrics/` because it depends on `AppInstrument.Meter`. `DriftConventions` (string constants) lives in `Domain.AI/Telemetry/Conventions/` as pure domain vocabulary.

5. **SafeExecuteAsync pattern:** Audit, notification, and graph operations are non-critical. Failures are logged but don't fail the evaluation. Matches `DefaultEscalationService` exactly.

---

## Implementation Notes (Post-Review)

**Review fixes applied:**

1. **GetAllNodesAsync replaced with GetNodesByOwnerAsync** — `GetDriftHistoryAsync` now queries by `OwnerId` instead of scanning all nodes. `PersistDriftEventAsync` sets `OwnerId = "{scope}:{scopeIdentifier}"` on graph nodes.

2. **Stopwatch-based duration recording** — `EvaluateDriftAsync` records `DriftMetrics.EvaluationDurationMs` on all 4 exit paths (disabled, no baseline, all-fail, success) via `Stopwatch.StartNew()`.

3. **DeserializeDriftScore logging** — Changed from `static` to instance method, bare `catch` replaced with `catch (Exception ex)` + `LogWarning`.

4. **BaselineId serialization** — `PersistDriftEventAsync` now includes `["BaselineId"]` in graph node Properties. `DeserializeDriftScore` parses it with `Guid.TryParse` fallback to `Guid.Empty`.

5. **Additional tests** — `EvaluateDrift_AllDimensionsFail_ReturnsFailure`, `EvaluateDrift_BaselineFallback_TaskTypeToSkillToAgent`, `EvaluateDrift_GraphNodeHasOwnerIdAndBaselineId`, `UpdateBaseline_InsufficientSamples_ReturnsFailure`, `GetDriftHistory_UsesGetNodesByOwnerAsync`.

**Actual file paths match plan.** 7 files created, 25 tests total (17 DefaultDriftDetectionServiceTests + 4 CompositeDriftNotifierTests + 4 DriftMetricsTests). 919 total Infrastructure.AI tests passing.

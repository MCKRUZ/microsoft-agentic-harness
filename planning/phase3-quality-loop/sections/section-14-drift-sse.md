# Section 14: AG-UI Drift Events and SSE Notifier

## Overview

This section adds drift detection notifications to the AG-UI SSE event stream. It creates four new AG-UI event DTOs for drift occurrences (warn, alert, escalate, resolved) and an `AgUiDriftNotifier` that translates domain drift events into those SSE frames. The implementation mirrors the existing `AgUiEscalationNotifier` pattern exactly: inject `IAgUiEventWriterAccessor`, gracefully no-op when no run is active, and catch non-cancellation exceptions.

**Layer:** Presentation.AgentHub (notifier + event DTOs), Application.AI.Common (interface — from section 05)

## Dependencies

- **Section 01 (Drift Domain Models):** `DriftScore`, `DriftEvent`, `DriftSeverity`, `DriftDimension`, `DriftDimensionScore`, `DriftResolution` records
- **Section 05 (Drift Interfaces):** `IDriftNotificationChannel` interface defining `NotifyDriftDetectedAsync` and `NotifyDriftResolvedAsync`
- **Existing infrastructure:** `IAgUiEventWriterAccessor`, `IAgUiEventWriter`, `AgUiEvent` base record, `AgUiEventType` constants (all in `Presentation.AgentHub.AgUi` namespace)

## Tests First

All tests go in `src/Content/Tests/Presentation.AgentHub.Tests/Notifications/AgUiDriftNotifierTests.cs`. Follow the exact same structure as the existing `AgUiEscalationNotifierTests.cs`.

```csharp
// File: src/Content/Tests/Presentation.AgentHub.Tests/Notifications/AgUiDriftNotifierTests.cs
// Namespace: Presentation.AgentHub.Tests.Notifications

// Test: AgUiDriftNotifier_DriftDetected_EmitsSseEvent
//   Arrange: create a DriftScore with severity Warn, multiple dimensions
//   Act: call NotifyDriftDetectedAsync
//   Assert: writer.WriteAsync called once with a DriftWarnEvent containing correct scope, 
//           scopeIdentifier, dimensions dict, maxDeviation, and severity string

// Test: AgUiDriftNotifier_DriftResolved_EmitsSseEvent
//   Arrange: create a DriftEvent with a DriftResolution (type: LearningApplied)
//   Act: call NotifyDriftResolvedAsync
//   Assert: writer.WriteAsync called once with a DriftResolvedEvent containing correct
//           eventId, resolution type string, resolvedBy ID, and resolvedAt timestamp

// Test: AgUiDriftNotifier_NoActiveWriter_NoOp
//   Arrange: set writerAccessor.Writer to null
//   Act: call NotifyDriftDetectedAsync
//   Assert: writer.WriteAsync never called (Times.Never)

// Test: AgUiDriftNotifier_Exception_LogsWarning_DoesNotThrow
//   Arrange: writer.WriteAsync throws InvalidOperationException("Stream closed")
//   Act: call NotifyDriftDetectedAsync
//   Assert: act.Should().NotThrowAsync()

// Test: DriftWarnEvent_SerializesCorrectFields
//   Arrange: construct a DriftWarnEvent with known values
//   Assert: all required JSON properties present (scope, scopeIdentifier, dimensions, 
//           maxDeviation, severity). Verify via record property assertions.

// Test: DriftAlertEvent_IncludesBaselineId
//   Arrange: construct a DriftAlertEvent with a baselineId
//   Assert: BaselineId property matches the supplied value

// Test: DriftEscalateEvent_IncludesEscalationId
//   Arrange: construct a DriftEscalateEvent with escalationId
//   Assert: EscalationId property matches the supplied value
```

**Test setup pattern** (mirrors `AgUiEscalationNotifierTests`):

- `Mock<IAgUiEventWriterAccessor>` with `Mock<IAgUiEventWriter>` wired to `.Writer`
- Default `_writerMock.Setup(w => w.WriteAsync(...)).Returns(Task.CompletedTask)`
- SUT constructed with `_accessorMock.Object` and `NullLogger<AgUiDriftNotifier>.Instance`
- For the no-writer test: create a separate SUT with accessor returning null
- For the exception test: reconfigure `_writerMock` to throw, then assert `NotThrowAsync()`
- Helper method `CreateDriftScore()` returning a sample `DriftScore` with at least two dimensions populated

**Event serialization tests** are simple record construction assertions. They don't need the writer mock -- they verify the DTO shape and that all required fields are init-accessible.

## Files to Create/Modify

| Action | File Path |
|--------|-----------|
| **Create** | `src/Content/Tests/Presentation.AgentHub.Tests/Notifications/AgUiDriftNotifierTests.cs` |
| **Create** | `src/Content/Presentation/Presentation.AgentHub/Notifications/AgUiDriftNotifier.cs` |
| **Modify** | `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventType.cs` — add 4 drift constants |
| **Modify** | `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEvents.cs` — add 4 `JsonDerivedType` attributes + 4 event record types |

## Implementation Details

### 1. AG-UI Event Type Constants

**File:** `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventType.cs`

Add four new constants to the existing `AgUiEventType` static class:

```csharp
/// <summary>Signals that drift was detected at warn severity.</summary>
public const string DriftWarn = "DRIFT_WARN";

/// <summary>Signals that drift was detected at alert severity.</summary>
public const string DriftAlert = "DRIFT_ALERT";

/// <summary>Signals that drift was detected at escalate severity.</summary>
public const string DriftEscalate = "DRIFT_ESCALATE";

/// <summary>Signals that a previously detected drift has been resolved.</summary>
public const string DriftResolved = "DRIFT_RESOLVED";
```

### 2. AG-UI Drift Event DTOs

**File:** `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEvents.cs`

Add four new `JsonDerivedType` attributes to the `AgUiEvent` base record, then define the four event records. These go in the same file as the existing escalation event DTOs, following the identical pattern.

**Polymorphic registration** -- add to the `AgUiEvent` attributes block:

```csharp
[JsonDerivedType(typeof(DriftWarnEvent), AgUiEventType.DriftWarn)]
[JsonDerivedType(typeof(DriftAlertEvent), AgUiEventType.DriftAlert)]
[JsonDerivedType(typeof(DriftEscalateEvent), AgUiEventType.DriftEscalate)]
[JsonDerivedType(typeof(DriftResolvedEvent), AgUiEventType.DriftResolved)]
```

**DriftWarnEvent** -- emitted when drift severity is `Warn`:

```csharp
/// <summary>
/// Signals that quality drift was detected at warning severity.
/// The agent's output quality has deviated from baseline but not critically.
/// </summary>
public sealed record DriftWarnEvent : AgUiEvent
{
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    [JsonPropertyName("scopeIdentifier")]
    public required string ScopeIdentifier { get; init; }

    [JsonPropertyName("dimensions")]
    public required IReadOnlyDictionary<string, double> Dimensions { get; init; }

    [JsonPropertyName("maxDeviation")]
    public required double MaxDeviation { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }
}
```

**DriftAlertEvent** -- extends warn-level info with `BaselineId`:

```csharp
/// <summary>
/// Signals that quality drift was detected at alert severity.
/// Includes the baseline ID for correlation with baseline store records.
/// </summary>
public sealed record DriftAlertEvent : AgUiEvent
{
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    [JsonPropertyName("scopeIdentifier")]
    public required string ScopeIdentifier { get; init; }

    [JsonPropertyName("dimensions")]
    public required IReadOnlyDictionary<string, double> Dimensions { get; init; }

    [JsonPropertyName("maxDeviation")]
    public required double MaxDeviation { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("baselineId")]
    public required string BaselineId { get; init; }
}
```

**DriftEscalateEvent** -- extends alert-level info with `EscalationId`:

```csharp
/// <summary>
/// Signals that quality drift was detected at escalation severity.
/// An escalation request has been triggered and is awaiting human review.
/// </summary>
public sealed record DriftEscalateEvent : AgUiEvent
{
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    [JsonPropertyName("scopeIdentifier")]
    public required string ScopeIdentifier { get; init; }

    [JsonPropertyName("dimensions")]
    public required IReadOnlyDictionary<string, double> Dimensions { get; init; }

    [JsonPropertyName("maxDeviation")]
    public required double MaxDeviation { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("baselineId")]
    public required string BaselineId { get; init; }

    [JsonPropertyName("escalationId")]
    public required string EscalationId { get; init; }
}
```

**DriftResolvedEvent** -- emitted when a drift event is resolved:

```csharp
/// <summary>
/// Signals that a previously detected drift has been resolved through
/// learning application, baseline adjustment, manual dismissal, or escalation resolution.
/// </summary>
public sealed record DriftResolvedEvent : AgUiEvent
{
    [JsonPropertyName("eventId")]
    public required string EventId { get; init; }

    [JsonPropertyName("resolutionType")]
    public required string ResolutionType { get; init; }

    [JsonPropertyName("resolvedBy")]
    public required string ResolvedBy { get; init; }

    [JsonPropertyName("resolvedAt")]
    public required DateTimeOffset ResolvedAt { get; init; }
}
```

### 3. AgUiDriftNotifier

**File:** `src/Content/Presentation/Presentation.AgentHub/Notifications/AgUiDriftNotifier.cs`

Implements `IDriftNotificationChannel` (from section 05, in `Application.AI.Common.Interfaces.DriftDetection`). Follow the `AgUiEscalationNotifier` pattern precisely.

**Constructor dependencies:**
- `IAgUiEventWriterAccessor writerAccessor`
- `ILogger<AgUiDriftNotifier> logger`

**`NotifyDriftDetectedAsync(DriftScore score, CancellationToken ct)`:**

1. Get writer from `_writerAccessor.Writer`. If null, log debug "No AG-UI writer active; skipping drift-detected event for {Scope}:{ScopeIdentifier}." and return.
2. Translate `DriftScore.Dimensions` (keyed by `DriftDimension` enum) into `IReadOnlyDictionary<string, double>` keyed by dimension name string, using each entry's `Deviation` value.
3. Map severity to the correct AG-UI event type:
   - `DriftSeverity.Warn` -> `DriftWarnEvent` with scope, scopeIdentifier, dimensions, maxDeviation, severity string
   - `DriftSeverity.Alert` -> `DriftAlertEvent` with same fields plus `BaselineId` from `score.BaselineId.ToString()`
   - `DriftSeverity.Escalate` -> `DriftEscalateEvent` with same fields plus `BaselineId` and `EscalationId` (empty string if not yet assigned -- the escalation ID comes from the drift service after escalation is queued)
   - `DriftSeverity.None` -> return without emitting (no SSE event for no-drift)
4. Wrap `writer.WriteAsync(evt, ct)` in try/catch: catch `Exception ex when (ex is not OperationCanceledException)`, log warning with the scope details.

**`NotifyDriftResolvedAsync(DriftEvent driftEvent, CancellationToken ct)`:**

1. Get writer. If null, log debug and return.
2. Guard: if `driftEvent.Resolution` is null, log warning "Cannot emit resolved event for unresolved drift {EventId}" and return.
3. Create `DriftResolvedEvent` with:
   - `EventId` = `driftEvent.EventId.ToString()`
   - `ResolutionType` = `driftEvent.Resolution.ResolvedBy.ToString()`
   - `ResolvedBy` = `driftEvent.Resolution.ResolutionId`
   - `ResolvedAt` = `driftEvent.Resolution.ResolvedAt`
4. Same try/catch exception handling pattern.

**Key design decision:** The `DriftEscalateEvent` needs an `EscalationId` but the escalation may not yet be queued at the time `NotifyDriftDetectedAsync` is called (the drift service calls the notifier before queuing escalation). The notifier should accept an optional `EscalationId` on the `DriftScore` or use `string.Empty` as a placeholder. The `DriftScore` domain record (section 01) does not carry an `EscalationId`, so the notifier should use `string.Empty` and rely on a subsequent `EscalationRequested` SSE event to provide the correlation ID to the client. Alternatively, the notifier can be called after escalation queuing in `DefaultDriftDetectionService`, in which case the escalation ID should be passed through the `DriftScore` or as metadata. The simplest approach: use `string.Empty` as a default and document that clients correlate drift-escalate events with escalation-requested events by timestamp and scope.

### 4. DI Registration (handled in Section 18)

The `AgUiDriftNotifier` will be registered in `Presentation.AgentHub/DependencyInjection.cs` as an `IDriftNotificationChannel` implementation, added to the composite `IDriftNotifier`. This registration is covered by section 18 -- this section only creates the implementation and tests.

## Verification

After implementation, run:

```powershell
dotnet build src/Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj
dotnet test src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj --filter "FullyQualifiedName~AgUiDriftNotifier"
```

All 7 tests should pass. The notifier depends on `IDriftNotificationChannel` (section 05) and domain types (section 01) being available at compile time. If those sections are not yet implemented, create minimal stub interfaces/records to unblock compilation, or implement this section after sections 01 and 05 are complete.

---

## Actual Implementation Notes

**Status:** Complete. Build green, 12 tests pass.

### Deviations from Plan

| # | Planned | Actual | Rationale |
|---|---------|--------|-----------|
| 1 | Plan specified 7 tests | 12 tests implemented | Code review identified 3 missing coverage areas: OperationCanceledException propagation, null-writer for resolved path, writer-throws for resolved path |
| 2 | `MapToEvent` allocated Dictionary before severity switch | Early return for `DriftSeverity.None` before allocation | Code review fix — avoided wasted allocation for the no-drift case |
| 3 | Plan mentioned DriftEscalateEvent with optional EscalationId | Used `string.Empty` sentinel as default | Matches plan's "simplest approach" — clients correlate with escalation-requested events by timestamp and scope |

### Test Coverage

| Test | Description |
|------|-------------|
| NotifyDriftDetectedAsync_WarnSeverity_WritesDriftWarnEvent | Warn → DriftWarnEvent with correct fields |
| NotifyDriftDetectedAsync_AlertSeverity_WritesDriftAlertEvent | Alert → DriftAlertEvent with BaselineId |
| NotifyDriftDetectedAsync_EscalateSeverity_WritesDriftEscalateEvent | Escalate → DriftEscalateEvent with empty EscalationId |
| NotifyDriftDetectedAsync_NoneSeverity_DoesNotWrite | None → no SSE event emitted |
| NotifyDriftResolvedAsync_WritesDriftResolvedEvent | Resolved drift → DriftResolvedEvent with resolution details |
| NotifyDriftResolvedAsync_NullResolution_DoesNotWrite | Unresolved drift → no event |
| NotifyDriftDetectedAsync_NoWriter_SilentlyReturns | No active run → silent no-op |
| NotifyDriftDetectedAsync_WriterThrows_CatchesAndDoesNotThrow | Non-cancellation exception → caught |
| NotifyDriftDetectedAsync_DimensionsMapCorrectly | Enum→string keys, Deviation values |
| NotifyDriftDetectedAsync_OperationCanceledException_Propagates | Cancellation → propagates (not swallowed) |
| NotifyDriftResolvedAsync_NoWriter_SilentlyReturns | No active run for resolved path |
| NotifyDriftResolvedAsync_WriterThrows_CatchesAndDoesNotThrow | Exception handling for resolved path |

### Code Review Trail

- Review: `planning/phase3-quality-loop/implementation/code_review/section-14-review.md`
- Interview: `planning/phase3-quality-loop/implementation/code_review/section-14-interview.md`

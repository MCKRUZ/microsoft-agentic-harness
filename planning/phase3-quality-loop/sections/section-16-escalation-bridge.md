# Section 16: Drift -> Escalation Integration (DriftEscalationBridge)

## Overview

This section implements the `DriftEscalationBridge`, an `IEscalationNotificationChannel` that listens for escalation resolutions and bridges them back into the drift detection and learnings systems. When a drift-originated escalation resolves, the bridge updates the corresponding drift event's resolution and optionally creates a learning entry from the correction.

The bridge is the reverse-direction complement to the forward-direction escalation trigger already built into `DefaultDriftDetectionService` (Section 8). Section 8 sends escalations out when drift severity hits `Escalate`. This section handles what happens when those escalations come back resolved.

**Layer:** Infrastructure.AI

**Depends on:** Section 8 (DefaultDriftDetectionService, IDriftDetectionService), Section 5 (drift interfaces, DriftEvent, DriftResolution), Section 1 (drift domain models), Section 6 (RememberCommand, LearningSourceType), Phase 2 (IEscalationNotificationChannel, EscalationOutcome, EscalationRequest)

---

## Background: Escalation Notification Channel Pattern

The escalation system uses a composite notifier pattern. `CompositeEscalationNotifier` implements `IEscalationNotifier` and fans out to all registered `IEscalationNotificationChannel` implementations. Existing channels include:

- `AgUiEscalationNotifier` (Presentation layer -- SSE events)
- `NoOpSlackNotifier` / `NoOpTeamsNotifier` (Infrastructure layer -- placeholder stubs)

The `DriftEscalationBridge` registers as another `IEscalationNotificationChannel`. When escalation events fire, the composite notifier calls all channels including this bridge.

Key contracts from Phase 2:

```csharp
// Application.AI.Common.Interfaces.Escalation
public interface IEscalationNotificationChannel
{
    Task NotifyEscalationRequestedAsync(EscalationRequest request, CancellationToken ct);
    Task NotifyEscalationResolvedAsync(EscalationOutcome outcome, CancellationToken ct);
    Task NotifyEscalationExpiringAsync(EscalationRequest request, TimeSpan remaining, CancellationToken ct);
}
```

The `EscalationRequest` record has a `ToolName` property (string). Section 8's `DefaultDriftDetectionService` uses the convention `ToolName = "drift_detection"` when building escalation requests for drift-originated escalations. The bridge filters on this convention.

The `EscalationOutcome` record has `EscalationId` (Guid), `IsApproved` (bool), `Decisions` (list of `ApproverDecision` with optional `Reason`), `ResolutionType`, and `ResolvedAt`.

---

## Forward Direction (Section 8, already built)

When `DefaultDriftDetectionService.EvaluateDriftAsync` determines severity == `Escalate` and `DriftConfig.EscalationEnabled` is true:

1. Build an `EscalationRequest` with `ToolName = "drift_detection"`, `RiskLevel.High`, description summarizing drifted dimensions
2. Call `IEscalationService.QueueEscalationAsync(request)`
3. Store the escalation ID on the `DriftEvent` for correlation

The escalation ID is stored on the `DriftEvent` (as part of graph node properties or within the event's data) so the bridge can correlate back.

---

## Reverse Direction (This Section)

### DriftEscalationBridge

**File:** `src/Content/Infrastructure/Infrastructure.AI/DriftDetection/DriftEscalationBridge.cs`

Implements `IEscalationNotificationChannel`. Injected dependencies:

- `IDriftDetectionService` -- to resolve drift events and update them
- `ISender` (MediatR) -- to dispatch `RememberCommand` for creating learnings
- `ILogger<DriftEscalationBridge>` -- structured logging
- `TimeProvider` -- for timestamps

**Behavior for `NotifyEscalationResolvedAsync`:**

1. **Filter:** Check if the escalation originated from drift detection. The bridge needs to correlate the `EscalationOutcome.EscalationId` back to a drift event. The convention is:
   - `DefaultDriftDetectionService` stores drift events with an `EscalationId` property when it queues an escalation
   - The bridge looks up drift events that reference this escalation ID
   - If no matching drift event is found, return early (this escalation was not drift-originated)

2. **Update drift resolution:** Create a `DriftResolution` record:
   - `ResolvedBy = DriftResolutionType.EscalationResolved`
   - `ResolutionId = outcome.EscalationId.ToString()`
   - `ResolvedAt = outcome.ResolvedAt`
   Update the drift event via the drift detection service.

3. **Create learning from corrections:** If the escalation outcome includes approver decisions with reasons (corrections/feedback), build a `RememberCommand`:
   - `Content`: aggregate the approver reasons into a correction summary
   - `Category`: `LearningCategory.FactualCorrection` (for approved corrections) or `LearningCategory.InstructionUpdate` (for denied/rejected)
   - `Scope`: derived from the drift event's scope (agent/skill/task type)
   - `Source`: `LearningSourceType.EscalationResolution`, `SourceId = outcome.EscalationId.ToString()`
   - `Provenance`: pipeline = `"DriftEscalationBridge"`, confidence = 0.8 for human corrections
   Dispatch via `ISender.Send(rememberCommand, ct)`

**Behavior for `NotifyEscalationRequestedAsync`:** Track drift-originated escalation IDs (see convention section below).

**Behavior for `NotifyEscalationExpiringAsync`:** No-op. The bridge only cares about resolutions.

**Error handling:** Wrap all operations in try/catch. Log warnings on failure. Never throw -- the composite notifier catches per-channel failures, but the bridge should be defensive anyway to avoid polluting logs with redundant exception chains.

### Convention: ToolName Filtering

The bridge uses the `ToolName == "drift_detection"` convention to identify drift-originated escalations. This requires the bridge to inspect the original `EscalationRequest` to determine the tool name. Since `NotifyEscalationResolvedAsync` only receives `EscalationOutcome` (which does not contain the original request), the bridge needs access to the original request.

Two approaches:
1. **Lookup via IEscalationService.GetPendingEscalationAsync** -- but by the time `NotifyEscalationResolvedAsync` fires, the escalation is already resolved and removed from active state
2. **Track drift escalation IDs internally** -- the bridge also receives `NotifyEscalationRequestedAsync`, so it can inspect the `ToolName` there and store the escalation ID in a `ConcurrentDictionary<Guid, EscalationRequest>`. Then in `NotifyEscalationResolvedAsync`, check if the ID is tracked.

Approach 2 is the correct design. The bridge:
- In `NotifyEscalationRequestedAsync`: if `request.ToolName == "drift_detection"`, store `request` keyed by `request.EscalationId`
- In `NotifyEscalationResolvedAsync`: if `_trackedDriftEscalations.TryRemove(outcome.EscalationId, out var request)`, proceed with resolution handling
- In `NotifyEscalationExpiringAsync`: no-op

This makes the bridge self-contained without requiring modifications to `EscalationOutcome`.

### Constant: DriftDetectionToolName

Define a constant `public const string DriftDetectionToolName = "drift_detection"` on the bridge class (or in a shared conventions class). This same constant must be used by `DefaultDriftDetectionService` when building escalation requests.

---

## Tests

**File:** `src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/DriftEscalationBridgeTests.cs`

All tests use xUnit, Moq, FluentAssertions. The test class mocks `IDriftDetectionService`, `ISender`, and `ILogger<DriftEscalationBridge>`. A `FakeTimeProvider` is used for deterministic timestamps.

```csharp
// Test: DriftEscalationBridge_DriftOriginated_UpdatesDriftEvent
//   Arrange: Create bridge, call NotifyEscalationRequestedAsync with ToolName="drift_detection",
//            then call NotifyEscalationResolvedAsync with same EscalationId
//   Assert: IDriftDetectionService method called to resolve the drift event with
//           DriftResolutionType.EscalationResolved and correct escalation ID

// Test: DriftEscalationBridge_DriftOriginated_CreatesLearning
//   Arrange: Create bridge, request with ToolName="drift_detection", resolve with
//            approver decisions that include Reason text
//   Assert: ISender.Send called with a RememberCommand containing:
//           - SourceType == LearningSourceType.EscalationResolution
//           - Content derived from approver reasons
//           - SourceId == escalation ID string

// Test: DriftEscalationBridge_NonDrift_IgnoresResolution
//   Arrange: Create bridge, call NotifyEscalationRequestedAsync with ToolName="some_other_tool",
//            then call NotifyEscalationResolvedAsync with same ID
//   Assert: No calls to IDriftDetectionService or ISender

// Test: DriftEscalationBridge_FiltersBy_ToolName_DriftDetection
//   Arrange: Create bridge, send two requests -- one with ToolName="drift_detection",
//            one with ToolName="code_execution". Resolve both.
//   Assert: Only the drift_detection escalation triggers drift resolution and learning creation

// Test: EscalationRequest_FromDrift_HasCorrectToolName
//   Arrange: Build an EscalationRequest with ToolName matching DriftEscalationBridge.DriftDetectionToolName
//   Assert: The constant value equals "drift_detection"

// Test: EscalationRequest_FromDrift_MapsRiskLevel
//   Arrange: Verify that the escalation request built by drift detection (documented in section 8)
//            maps DriftSeverity.Escalate to RiskLevel.High
//   Assert: This is a documentation/contract test confirming the mapping convention
```

### Test Helper Methods

The test class should include:
- `CreateTestRequest(string toolName)` -- builds an `EscalationRequest` with configurable `ToolName`
- `CreateTestOutcome(Guid escalationId, bool approved, string? reason)` -- builds an `EscalationOutcome`
- `CreateSut()` -- constructs the bridge with mocked dependencies

---

## Implementation Details

### Class Skeleton

```csharp
namespace Infrastructure.AI.DriftDetection;

/// <summary>
/// Bridges escalation resolutions back into the drift detection and learnings systems.
/// When a drift-originated escalation resolves, updates the drift event and optionally
/// creates a learning entry from approver corrections.
/// </summary>
/// <remarks>
/// Registered as an <see cref="IEscalationNotificationChannel"/> in DI. The
/// <see cref="CompositeEscalationNotifier"/> automatically fans out to this channel.
/// Only processes escalations where <c>ToolName == "drift_detection"</c>.
/// </remarks>
public sealed class DriftEscalationBridge : IEscalationNotificationChannel
{
    /// <summary>Convention: tool name used by drift detection when queuing escalations.</summary>
    public const string DriftDetectionToolName = "drift_detection";

    private readonly ConcurrentDictionary<Guid, EscalationRequest> _trackedDriftEscalations = new();
    private readonly IDriftDetectionService _driftService;
    private readonly ISender _sender;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DriftEscalationBridge> _logger;

    // Constructor injects IDriftDetectionService, ISender, TimeProvider, ILogger

    // NotifyEscalationRequestedAsync: if ToolName matches, track the request
    // NotifyEscalationResolvedAsync: if tracked, resolve drift event + create learning
    // NotifyEscalationExpiringAsync: no-op (return Task.CompletedTask)
}
```

### Learning Creation Logic

When creating a learning from an escalation resolution:

1. Collect all `ApproverDecision` entries from `outcome.Decisions` where `Reason` is not null/empty
2. If no reasons provided, skip learning creation (no correction content to capture)
3. Build the `Content` string: concatenate approver reasons (e.g., "Correction from {approverName}: {reason}")
4. Determine `LearningCategory`:
   - If `outcome.IsApproved`: `LearningCategory.InstructionUpdate` (the drift was acknowledged and the new behavior accepted)
   - If not approved: `LearningCategory.FactualCorrection` (the drift was a genuine regression)
5. Build `LearningScope` from the drift event's scope information
6. Dispatch `RememberCommand` via MediatR

### DI Registration

In `Infrastructure.AI/DependencyInjection.cs`, add the bridge as another `IEscalationNotificationChannel`:

```csharp
services.AddSingleton<IEscalationNotificationChannel, DriftEscalationBridge>();
```

This registration sits alongside `NoOpSlackNotifier` and `NoOpTeamsNotifier`. The `CompositeEscalationNotifier` discovers all `IEscalationNotificationChannel` registrations automatically via `IEnumerable<IEscalationNotificationChannel>` injection.

Note: The full DI registration is handled in Section 18, but the bridge class itself must be designed to be registrable as a singleton `IEscalationNotificationChannel`.

---

## File Manifest

| File | Action | Description |
|------|--------|-------------|
| `src/Content/Infrastructure/Infrastructure.AI/DriftDetection/DriftEscalationBridge.cs` | Create | Bridge implementation |
| `src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/DriftEscalationBridgeTests.cs` | Create | Unit tests |

---

## Edge Cases and Design Decisions

1. **Race condition on tracked IDs:** `ConcurrentDictionary` handles thread safety. The `TryRemove` in `NotifyEscalationResolvedAsync` ensures exactly-once processing even if the composite notifier somehow dispatches twice.

2. **Escalation resolved before requested notification:** In the composite fan-out, `NotifyEscalationRequestedAsync` fires before `NotifyEscalationResolvedAsync` (the escalation service calls `RecordAndNotifyRequestAsync` during creation and `ResolveEscalationAsync` during resolution). So the tracking dictionary will always be populated before resolution fires.

3. **Memory leak from unresolved escalations:** If an escalation is requested but never resolved (process crash), the tracked request stays in the dictionary. Since escalations have timeouts (default 300s), this is bounded. For production hardening, a periodic cleanup of entries older than max timeout could be added, but is not required for this implementation.

4. **Circular dependency prevention:** The bridge injects `IDriftDetectionService` and `ISender`. The drift detection service does NOT inject the bridge. The escalation flow is: DriftService -> IEscalationService -> CompositeNotifier -> DriftEscalationBridge -> IDriftDetectionService (for resolution). This is a callback-style cycle through the notification system, not a constructor injection cycle. DI resolves correctly because all are singletons and the bridge never calls back into the escalation path.

5. **Constant sharing:** `DriftEscalationBridge.DriftDetectionToolName` is the single source of truth for the `"drift_detection"` tool name convention. `DefaultDriftDetectionService` (Section 8) should reference this constant when building escalation requests rather than using a string literal.

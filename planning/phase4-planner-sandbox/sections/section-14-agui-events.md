# Section 14: AG-UI Events

## Overview

This section adds planner-specific AG-UI event types and an `AgUiPlanProgressNotifier` that implements the `IPlanProgressNotifier` interface (defined in section 02). The notifier translates plan execution lifecycle events into AG-UI SSE frames, following the exact patterns established by `AgUiDriftNotifier`, `AgUiEscalationNotifier`, and `AgUiLearningNotifier`.

Events flow through the existing `AgUiEventWriter` and SSE endpoint. No new transport is needed.

## Dependencies

- **Section 01 (Domain Models)**: `PlanId`, `PlanStepId`, `StepType`, `StepExecutionStatus`, `SandboxIsolationLevel` types used in event payloads
- **Section 02 (Application Interfaces)**: `IPlanProgressNotifier` interface that this section implements

## Architecture

The pattern is identical to the existing notification channels:

1. `IPlanProgressNotifier` is defined in `Application.AI.Common/Interfaces/Planner/` (section 02)
2. `AgUiPlanProgressNotifier` implements it in `Presentation.AgentHub/Planner/`
3. Infrastructure executors (PlanExecutor, step executors) call `IPlanProgressNotifier` methods -- never Presentation types directly
4. The notifier obtains the current `IAgUiEventWriter` via `IAgUiEventWriterAccessor` (AsyncLocal-scoped)
5. If no writer is active (non-SSE context, e.g., ConsoleUI), the notifier silently skips emission
6. Writer exceptions (except `OperationCanceledException`) are caught and logged as warnings, never propagated

---

## Tests First

### File: `src/Content/Tests/Presentation.AgentHub.Tests/Planner/AgUiPlanProgressNotifierTests.cs`

```csharp
// Test fixture: Mock<IAgUiEventWriterAccessor>, Mock<IAgUiEventWriter>, NullLogger
// SUT: AgUiPlanProgressNotifier

// Test: AgUiPlanProgressNotifier_PlanStarted_EmitsPlanStartedEvent
// Test: AgUiPlanProgressNotifier_StepStarted_EmitsStepStartedEvent
// Test: AgUiPlanProgressNotifier_StepCompleted_EmitsStepCompletedEvent
// Test: AgUiPlanProgressNotifier_StateUpdate_EmitsStateDeltaEvent
// Test: AgUiPlanProgressNotifier_SandboxStatus_EmitsSandboxStatusEvent
// Test: AgUiPlanProgressNotifier_PlanCompleted_EmitsPlanCompletedEvent
// Test: AgUiPlanProgressNotifier_PlanFailed_EmitsPlanFailedEvent

// Edge cases (following existing notifier test patterns):
// Test: NoWriter_SilentlyReturns
// Test: WriterThrows_CatchesAndDoesNotThrow
// Test: OperationCanceledException_Propagates
```

### File: `src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiPlanEventSerializationTests.cs`

```csharp
// Test: PlanStartedEvent_Serializes_WithCorrectTypeDiscriminator
// Test: PlanStepStartedEvent_Serializes_WithStepFields
// Test: PlanStepCompletedEvent_Serializes_WithStatusAndDuration
// Test: PlanStateUpdateEvent_Serializes_WithPatchOperations
// Test: SandboxStatusEvent_Serializes_AsCustomType
// Test: PlanCompletedEvent_Serializes_WithSummary
// Test: PlanFailedEvent_Serializes_WithErrorDetails
// Test: AllPlanEvents_RoundTrip_DeserializeToCorrectSubtype
```

---

## Implementation Details

### 1. AG-UI Event Type Constants

**File**: `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventType.cs` (modify)

Add these constants to the existing `AgUiEventType` static class:

```csharp
public const string PlanStarted = "PLAN_STARTED";
public const string PlanStepStarted = "PLAN_STEP_STARTED";
public const string PlanStepCompleted = "PLAN_STEP_COMPLETED";
public const string PlanStateDelta = "PLAN_STATE_DELTA";
public const string SandboxStatus = "SANDBOX_STATUS";
public const string PlanCompleted = "PLAN_COMPLETED";
public const string PlanFailed = "PLAN_FAILED";
```

### 2. AG-UI Event Subtypes

**File**: `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEvents.cs` (modify)

Add `JsonDerivedType` attributes to the `AgUiEvent` base record for each new subtype, then define:

**`PlanStartedEvent`** -- `PLAN_STARTED`. Payload: `planId`, `planName`, `totalSteps`.

**`PlanStepStartedEvent`** -- `PLAN_STEP_STARTED`. Payload: `planId`, `stepId`, `stepName`, `stepType`.

**`PlanStepCompletedEvent`** -- `PLAN_STEP_COMPLETED`. Payload: `planId`, `stepId`, `status`, `durationMs`, `outputSummary` (truncated for wire).

**`PlanStateUpdateEvent`** -- `PLAN_STATE_DELTA`. Payload: `planId`, `patch` (IReadOnlyList<JsonPatchOperation>). RFC 6902 JSON Patch array encoding step status transitions.

```csharp
public sealed record JsonPatchOperation
{
    [JsonPropertyName("op")]
    public required string Op { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("value")]
    public required object Value { get; init; }
}
```

**`SandboxStatusEvent`** -- `SANDBOX_STATUS`. Payload: `planId`, `stepId`, `toolName`, `isolationLevel`, `memoryUsedBytes`, `cpuTimeMs`, `attestationHash`.

**`PlanCompletedEvent`** -- `PLAN_COMPLETED`. Payload: `planId`, `totalDurationMs`, `completedSteps`, `failedSteps`, `skippedSteps`.

**`PlanFailedEvent`** -- `PLAN_FAILED`. Payload: `planId`, `failedStepId`, `errorMessage`.

### 3. AgUiPlanProgressNotifier Implementation

**File**: `src/Content/Presentation/Presentation.AgentHub/Planner/AgUiPlanProgressNotifier.cs` (create)

Constructor dependencies:
- `IAgUiEventWriterAccessor`
- `ILogger<AgUiPlanProgressNotifier>`

Each method follows the same template (identical to `AgUiDriftNotifier`):

1. Get writer from accessor. If null, log debug and return.
2. Map parameters to the corresponding AG-UI event record.
3. Try-catch `writer.WriteAsync(evt, ct)`:
   - `OperationCanceledException` is NOT caught (re-throws)
   - All other exceptions are caught, logged as warning, and swallowed

The `NotifyStateUpdateAsync` method constructs the JSON Patch array:

```csharp
var patch = new List<JsonPatchOperation>
{
    new()
    {
        Op = "replace",
        Path = $"/steps/{stepId}/status",
        Value = newStatus,
    }
};
```

### 4. DI Registration

**File**: `src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs` (modify)

Add alongside existing notifier registrations:

```csharp
services.AddSingleton<IPlanProgressNotifier, AgUiPlanProgressNotifier>();
```

Singleton lifetime matches `AgUiDriftNotifier`, `AgUiEscalationNotifier`, `AgUiLearningNotifier`.

---

## File Summary

| File | Action | Purpose |
|------|--------|---------|
| `Presentation.AgentHub/AgUi/AgUiEventType.cs` | Modify | Add 7 new event type constants |
| `Presentation.AgentHub/AgUi/AgUiEvents.cs` | Modify | Add 7 `JsonDerivedType` attributes + 7 event records + `JsonPatchOperation` |
| `Presentation.AgentHub/Planner/AgUiPlanProgressNotifier.cs` | Create | `IPlanProgressNotifier` implementation |
| `Presentation.AgentHub/DependencyInjection.cs` | Modify | Register notifier |
| `Presentation.AgentHub.Tests/Planner/AgUiPlanProgressNotifierTests.cs` | Create | Notifier unit tests |
| `Presentation.AgentHub.Tests/AgUi/AgUiPlanEventSerializationTests.cs` | Create | Serialization tests |

---

## Implementation Notes

- **`SandboxStatusEvent` uses dedicated discriminator**: Following the existing codebase convention of custom discriminators (`ESCALATION_REQUESTED`, `DRIFT_WARN`), use `SANDBOX_STATUS` directly rather than the generic AG-UI `CUSTOM` bucket.

- **JSON Patch simplicity**: Only `replace` operation is used for step status transitions. No external library needed.

- **Output summary truncation**: `PlanStepCompletedEvent.OutputSummary` should be truncated to 500 characters by the notifier. Full output via `GetPlanHistoryQuery`.

- **Wire format**: All `[JsonPropertyName]` values use camelCase. Enums serialized as strings. IDs as `Guid.ToString()` strings.

---

## Implementation Notes (Post-Build)

### Deviations from Plan

1. **Notifier placed in `Planner/` not `Notifications/`** — followed the spec as written. Existing notifiers live in `Notifications/` but the planner notifier was spec'd for `Planner/` to group planner code together.

2. **`TryWriteAsync` helper method** — extracted the repeated try/catch pattern into a single `TryWriteAsync(evt, eventKind, planId, ct)` method instead of inlining in each method. This is cleaner than the existing notifiers (which inline the pattern). Reviewer noted this as a positive evolution.

3. **`PlanCompletedEvent` simplified** — spec included `completedSteps`, `failedSteps`, `skippedSteps` counts, but these require data the notifier doesn't receive (the interface only passes `PlanId` + `TimeSpan`). Kept the event minimal with just `planId` + `totalDurationMs`. Step counts can be derived client-side from preceding step events.

### Code Review Fixes Applied

1. **Error message truncation** — Added `Truncate()` to `errorMessage` in `NotifyPlanFailedAsync` to prevent potential stack trace leaks through SSE (matching the 500-char cap on `OutputSummary`).

2. **Null attestation hash test** — Added `SandboxStatus_NullAttestationHash_PassesNull` test to cover the null path in the notifier (serialization test already covered omission).

### Test Summary

- **24 tests total**, all passing
- 12 notifier behavior tests (7 happy path + truncation + no-writer + writer-throws + OperationCanceledException + null attestation)
- 12 serialization tests (7 discriminator checks + 3 round-trips + 2 null field omission)

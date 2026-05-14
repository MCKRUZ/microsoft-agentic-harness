# Section 08: Escalation Service Implementation

## Overview

This section implements `DefaultEscalationService`, the core orchestrator for the human escalation lifecycle. It is the single entry point called by the governance pipeline (section-17) when an agent action requires human approval. The service manages in-memory escalation state, races timeout against human decisions, evaluates approval strategies, dispatches notifications, and records audit events.

**Layer:** `Infrastructure.AI`
**File:** `src/Content/Infrastructure/Infrastructure.AI/Escalation/DefaultEscalationService.cs`
**Namespace:** `Infrastructure.AI.Escalation`

---

## Dependencies

| Dependency | Section | What It Provides |
|------------|---------|-----------------|
| Domain escalation models | section-01 | `EscalationRequest`, `EscalationOutcome`, `ApproverDecision`, `ApprovalEvaluation`, all enums (`EscalationPriority`, `EscalationResolutionType`, `EscalationTimeoutAction`, `ApprovalStrategyType`) |
| OTel conventions & metrics | section-03 | `EscalationConventions` (attribute constants), `EscalationMetrics` (static instrument instances: `Requests`, `Resolutions`, `DurationMs`, `Timeouts`, `Pending`, `ApproverResponseMs`) |
| Configuration | section-04 | `EscalationConfig` (default timeout, default strategy, priority overrides) |
| Approval strategies | section-05 | `IApprovalStrategy` (keyed by `ApprovalStrategyType`), `ApprovalEvaluation` return type |
| Escalation interfaces | section-06 | `IEscalationService` (the interface this class implements), `IEscalationNotifier`, `IEscalationAuditStore` |

**Blocks:** section-17 (governance integration), section-19 (DI registration).

---

## Background

The codebase already has a governance approval workflow at `Application.Core/Workflows/Governance/` using MAF's `RequestPort<ApprovalRequest, ApprovalResponse>` pattern. `DefaultEscalationService` is the orchestrator that constructs `EscalationRequest` records, manages the approval lifecycle, and feeds resolved outcomes back through the existing workflow.

### Known Limitation: In-Memory State

`DefaultEscalationService` stores active escalations in an in-memory `ConcurrentDictionary`. If the process restarts while an escalation is pending, the in-memory state is lost. The `JsonlEscalationAuditStore` (section-09) records all events for compliance, but there is no automatic recovery path. On startup, the service logs a warning if the audit store contains unresolved entries. Durable state recovery is a Phase 3+ concern.

### Pattern Reference: CapabilityMatchSupervisor

The implementation follows patterns established by `CapabilityMatchSupervisor` at `src/Content/Infrastructure/Infrastructure.AI/Agents/CapabilityMatchSupervisor.cs`:
- `ConcurrentDictionary` for active work tracking
- `CancellationTokenSource.CreateLinkedTokenSource()` for caller cancellation propagation
- `Stopwatch` + OTel metrics recording on completion
- `IDisposable` for cleanup of active CTS instances

---

## Tests First

All tests go in `src/Content/Tests/Infrastructure.AI.Tests/Escalation/DefaultEscalationServiceTests.cs`. Testing framework: xUnit + Moq + FluentAssertions. Naming: `MethodName_Scenario_ExpectedResult`.

### Test Setup

The test class mocks:
- `IApprovalStrategy` -- resolved via a `Dictionary<ApprovalStrategyType, IApprovalStrategy>` or individual mocks for different strategies
- `IEscalationNotifier` -- verify notification calls
- `IEscalationAuditStore` -- verify audit recording calls
- `IOptionsMonitor<EscalationConfig>` -- provide config values
- `ILogger<DefaultEscalationService>` -- standard Moq logger mock

The SUT (`DefaultEscalationService`) is constructed with these mocks. Helper methods build valid `EscalationRequest` and `ApproverDecision` instances for test scenarios.

### Test Stubs

```csharp
// File: src/Content/Tests/Infrastructure.AI.Tests/Escalation/DefaultEscalationServiceTests.cs

namespace Infrastructure.AI.Tests.Escalation;

/// <summary>
/// Tests for <see cref="DefaultEscalationService"/>.
/// Verifies escalation lifecycle: creation, strategy evaluation, timeout racing,
/// cancellation propagation, notification dispatch, and audit recording.
/// </summary>
public sealed class DefaultEscalationServiceTests : IDisposable
{
    // --- Fields ---
    // Mock<IEscalationNotifier> _notifier
    // Mock<IEscalationAuditStore> _auditStore
    // Mock<IApprovalStrategy> _anyOfStrategy (returns ApprovalStrategyType.AnyOf)
    // IOptionsMonitor<EscalationConfig> _config
    // DefaultEscalationService _sut

    // --- Helpers ---
    // EscalationRequest CreateTestRequest(...)  -- builds minimal valid request
    // ApproverDecision CreateApproval(string approverName)  -- builds approval decision
    // ApproverDecision CreateDenial(string approverName)  -- builds denial decision

    // ===== RequestEscalationAsync =====

    // Test: RequestEscalationAsync_CreatesEscalation_NotifiesApprovers
    //   Arrange: Create request with 2 approvers, strategy=AnyOf.
    //            Mock strategy to return resolved+approved on first decision.
    //            After calling RequestEscalationAsync on background task,
    //            submit an approval decision.
    //   Act: Await the blocking request.
    //   Assert: _notifier.NotifyEscalationRequestedAsync called once with the request.
    //           Result is EscalationOutcome with IsApproved == true.

    // Test: RequestEscalationAsync_BlockingMode_AwaitsOutcome
    //   Arrange: Create request, strategy never resolves (returns IsResolved=false).
    //            Start RequestEscalationAsync, then submit a resolving decision after delay.
    //   Act: Await. Measure elapsed time.
    //   Assert: Result arrived only after the decision was submitted, not before.

    // Test: RequestEscalationAsync_AuditsRequest
    //   Arrange: Create request.
    //   Act: Start RequestEscalationAsync, then resolve it.
    //   Assert: _auditStore.RecordRequestAsync called once with the request.

    // ===== QueueEscalationAsync =====

    // Test: QueueEscalationAsync_ReturnsEscalationId_DoesNotBlock
    //   Arrange: Create request.
    //   Act: Call QueueEscalationAsync.
    //   Assert: Returns the request's EscalationId. Call completes immediately
    //           (no blocking). _notifier.NotifyEscalationRequestedAsync still called.

    // ===== SubmitDecisionAsync =====

    // Test: SubmitDecisionAsync_TriggersStrategyEvaluation_ReturnsOutcomeIfResolved
    //   Arrange: Create and queue a request with AnyOf strategy.
    //            Mock strategy to return resolved+approved when 1 decision arrives.
    //   Act: SubmitDecisionAsync with an approval.
    //   Assert: Returns non-null EscalationOutcome with IsApproved==true.
    //           _auditStore.RecordDecisionAsync called. _auditStore.RecordOutcomeAsync called.
    //           _notifier.NotifyEscalationResolvedAsync called.

    // Test: SubmitDecisionAsync_PartialDecision_ReturnsNull
    //   Arrange: Create and queue a request with AllOf strategy (3 approvers).
    //            Mock strategy to return IsResolved=false for 1 decision.
    //   Act: SubmitDecisionAsync with 1 approval.
    //   Assert: Returns null. _auditStore.RecordDecisionAsync called.
    //           _notifier.NotifyEscalationResolvedAsync NOT called.

    // Test: SubmitDecisionAsync_UnknownEscalationId_ReturnsNull
    //   Act: SubmitDecisionAsync with a random Guid.
    //   Assert: Returns null. No mock calls.

    // ===== Timeout =====

    // Test: Timeout_FiresDenyAndEscalate_CompletesWithTimedOut
    //   Arrange: Create request with TimeoutSeconds=1 (short), TimeoutAction=DenyAndEscalate.
    //   Act: RequestEscalationAsync -- don't submit any decisions.
    //   Assert: Returns EscalationOutcome with ResolutionType==TimedOut, IsApproved==false.
    //           EscalationMetrics.Timeouts incremented.
    //           _auditStore.RecordOutcomeAsync called.

    // Test: Timeout_CallerCancelled_PropagatesCancellation
    //   Arrange: Create request with long timeout (300s).
    //            Create a CancellationTokenSource, cancel it after 100ms.
    //   Act: RequestEscalationAsync with the cancellable token.
    //   Assert: Throws OperationCanceledException.
    //           The escalation is removed from pending state.

    // Test: Timeout_AuditsOutcome
    //   Arrange: Create request with TimeoutSeconds=1, TimeoutAction=Deny.
    //   Act: RequestEscalationAsync, let it time out.
    //   Assert: _auditStore.RecordOutcomeAsync called with ResolutionType==TimedOut.

    // ===== Concurrency =====

    // Test: ConcurrentDecisions_ThreadSafe_NoRaceConditions
    //   Arrange: Create request with QuorumStrategy (2-of-3).
    //            Queue the escalation.
    //   Act: Submit 3 decisions concurrently via Task.WhenAll.
    //   Assert: Exactly one call returns a non-null EscalationOutcome.
    //           _auditStore.RecordOutcomeAsync called exactly once.

    // ===== GetPending =====

    // Test: GetPendingEscalationsAsync_ReturnsOnlyPending
    //   Arrange: Queue 3 escalations. Resolve 1 of them.
    //   Act: GetPendingEscalationsAsync for an approver on all 3.
    //   Assert: Returns only the 2 unresolved escalations.

    // Test: GetPendingEscalationAsync_ResolvedEscalation_ReturnsNull
    //   Arrange: Queue escalation, then resolve it.
    //   Act: GetPendingEscalationAsync with the resolved escalation's ID.
    //   Assert: Returns null.

    // ===== Metrics =====

    // Test: RequestEscalationAsync_IncrementsRequestsCounter
    //   Arrange: Create and queue request.
    //   Assert: EscalationMetrics.Requests incremented (verify via metric listener or
    //           structured log verification depending on test infrastructure).

    // Test: Resolution_RecordsDurationHistogram
    //   Arrange: Queue request, submit resolving decision.
    //   Assert: EscalationMetrics.DurationMs recorded with positive value.

    // --- IDisposable ---
    // Dispose: dispose the SUT to clean up any active CTS instances.
}
```

**Key testing patterns:**
- For blocking `RequestEscalationAsync` tests, start the call on a `Task.Run` and then submit decisions from the test thread. Use `Task.WhenAny` with a short timeout to detect unexpected blocking.
- For timeout tests, use very short timeouts (1 second) to keep tests fast.
- For concurrency tests, use `Task.WhenAll` with multiple `SubmitDecisionAsync` calls and assert exactly one returns a non-null outcome.
- Mock `IApprovalStrategy.EvaluateDecision` to control when escalations resolve.

---

## Implementation Details

### File to Create

`src/Content/Infrastructure/Infrastructure.AI/Escalation/DefaultEscalationService.cs`

### Class Design

`DefaultEscalationService` is a `sealed class` implementing `IEscalationService` and `IDisposable`.

### Constructor Dependencies

```csharp
/// <summary>
/// Orchestrates the escalation lifecycle: creation, approval tracking,
/// timeout management, notification dispatch, and audit recording.
/// </summary>
/// <remarks>
/// Active escalations are held in memory. Process restart loses pending state.
/// The <see cref="IEscalationAuditStore"/> provides durable compliance records,
/// but automatic recovery from audit logs is not implemented (Phase 3+).
/// </remarks>
public sealed class DefaultEscalationService : IEscalationService, IDisposable
```

Injected dependencies:
- `IServiceProvider` -- to resolve `IApprovalStrategy` by keyed DI (`ApprovalStrategyType`)
- `IEscalationNotifier` -- fan-out notifications to all channels
- `IEscalationAuditStore` -- compliance audit trail
- `IOptionsMonitor<EscalationConfig>` -- escalation defaults and priority overrides
- `ILogger<DefaultEscalationService>` -- structured logging

### Internal State

An `EscalationState` private record (or nested class) tracks each active escalation:

```csharp
/// <summary>Tracks the mutable state of an active escalation.</summary>
private sealed class EscalationState
{
    public required EscalationRequest Request { get; init; }
    public List<ApproverDecision> Decisions { get; } = [];
    public TaskCompletionSource<EscalationOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationTokenSource TimeoutCts { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public readonly object Lock = new();
}
```

The `ConcurrentDictionary<Guid, EscalationState>` holds active escalations, keyed by `EscalationId`.

**Why `TaskCreationOptions.RunContinuationsAsynchronously`:** Prevents continuations from running synchronously on the thread that completes the TCS, which could cause deadlocks if that thread holds the `Lock`.

### Method Implementations

#### `RequestEscalationAsync` (blocking mode)

1. Create `EscalationState` with request, resolve `IApprovalStrategy` via `IServiceProvider.GetRequiredKeyedService<IApprovalStrategy>(request.ApprovalStrategy)`.
2. Add to `ConcurrentDictionary`.
3. Increment `EscalationMetrics.Requests` and `EscalationMetrics.Pending`.
4. Record request via `IEscalationAuditStore.RecordRequestAsync`.
5. Notify approvers via `IEscalationNotifier.NotifyEscalationRequestedAsync`.
6. Start timeout race (see Timeout Implementation below).
7. Link caller's `CancellationToken` to the escalation's `CancellationTokenSource` via `CancellationTokenSource.CreateLinkedTokenSource()`.
8. `await state.Completion.Task` -- blocks until human decision or timeout resolves it.
9. On completion: decrement `EscalationMetrics.Pending`, record `EscalationMetrics.DurationMs`.
10. Return `EscalationOutcome`.

#### `QueueEscalationAsync` (non-blocking mode)

Steps 1-6 are identical to `RequestEscalationAsync`. Instead of awaiting the TCS, return `request.EscalationId` immediately. The timeout race runs in the background. The TCS is still completed on resolution, allowing `SubmitDecisionAsync` to function.

#### `SubmitDecisionAsync`

1. Look up `EscalationState` from `ConcurrentDictionary` by `escalationId`. Return `null` if not found.
2. Record the decision via `IEscalationAuditStore.RecordDecisionAsync`.
3. Record `EscalationMetrics.ApproverResponseMs` (elapsed since `EscalationState.CreatedAt`).
4. **Thread-safe decision evaluation:** Acquire `lock (state.Lock)` to prevent concurrent strategy evaluation:
   - Add decision to `state.Decisions`.
   - Call `strategy.EvaluateDecision(state.Request, state.Decisions.AsReadOnly())`.
   - If `evaluation.IsResolved`: build `EscalationOutcome`, call `ResolveEscalation(state, outcome)`.
5. If resolved, return the `EscalationOutcome`. Otherwise return `null`.

**Why `lock` instead of `SemaphoreSlim`:** The critical section is purely CPU-bound (list add + strategy evaluation). No async work inside the lock. `lock` is simpler and lower overhead than a semaphore for this use case. The `ConcurrentDictionary` handles concurrent lookups; the per-escalation `lock` only serializes decisions on the same escalation.

#### `GetPendingEscalationAsync` / `GetPendingEscalationsAsync`

Simple reads from the `ConcurrentDictionary`. `GetPendingEscalationAsync` returns `state.Request` if found, else `null`. `GetPendingEscalationsAsync` filters by `state.Request.Approvers.Contains(approverName)`.

#### Private: `ResolveEscalation`

Called when a decision or timeout resolves an escalation:

1. Cancel the timeout via `state.TimeoutCts.Cancel()`.
2. Remove from `ConcurrentDictionary`.
3. Record outcome via `IEscalationAuditStore.RecordOutcomeAsync`.
4. Notify via `IEscalationNotifier.NotifyEscalationResolvedAsync`.
5. Record metrics: `EscalationMetrics.Resolutions` (tagged by `ResolutionType` and `Priority`), `EscalationMetrics.DurationMs`.
6. Decrement `EscalationMetrics.Pending`.
7. Complete `state.Completion.TrySetResult(outcome)`.

### Timeout Implementation

The timeout is implemented as a `Task.Delay` that races against the `TaskCompletionSource`. No background timer service is needed -- the timeout is scoped to the escalation lifetime.

```
// Pseudocode -- not exact implementation
async Task RunTimeoutAsync(EscalationState state)
{
    try
    {
        await Task.Delay(TimeSpan.FromSeconds(state.Request.TimeoutSeconds), state.TimeoutCts.Token);
        // Timeout won the race
        HandleTimeout(state);
    }
    catch (OperationCanceledException)
    {
        // The escalation was resolved before timeout -- normal path, do nothing.
    }
}
```

This is fire-and-forget from the caller's perspective. Use `_ = RunTimeoutAsync(state)` (discarded task) with the timeout method catching its own exceptions. The `TimeoutCts` is cancelled in `ResolveEscalation` when a human decision arrives first.

**Timeout action handling** (based on `EscalationTimeoutAction`):
- `Deny`: Build outcome with `ResolutionType.TimedOut`, `IsApproved=false`. Complete TCS.
- `DenyAndEscalate`: Same as Deny, but set `EscalatedToTier` on the outcome. Log the escalation.
- `Approve`: Build outcome with `ResolutionType.TimedOut`, `IsApproved=true`. Complete TCS. (Auto-approve on timeout -- used for low-risk informational escalations.)
- `Escalate`: Don't resolve. Instead, notify next-tier approvers. (Future enhancement -- for now, treat as `DenyAndEscalate`.)

Increment `EscalationMetrics.Timeouts` on any timeout resolution.

### Cancellation Propagation

When `RequestEscalationAsync` is called, the caller's `CancellationToken` is linked to the escalation's `CancellationTokenSource`:

```
using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, state.TimeoutCts.Token);
```

If the caller disconnects (e.g., HTTP request cancelled, SignalR disconnect), the linked token fires, which:
1. Cancels the `Task.Delay` in the timeout runner.
2. The `state.Completion.TrySetCanceled()` path fires.
3. The `RequestEscalationAsync` awaiter receives `OperationCanceledException`.
4. The escalation is removed from the `ConcurrentDictionary` (no zombie escalations).

### Approval Strategy Resolution

The service resolves `IApprovalStrategy` from DI using keyed services:

```csharp
var strategy = _serviceProvider.GetRequiredKeyedService<IApprovalStrategy>(request.ApprovalStrategy);
```

This resolves `AnyOfApprovalStrategy`, `AllOfApprovalStrategy`, or `QuorumApprovalStrategy` based on the `ApprovalStrategyType` enum value from the `EscalationRequest`. The keyed DI registration is done in section-19.

### OTel Instrumentation Points

The service instruments every lifecycle event using the static `EscalationMetrics` class (from section-03):

| Event | Metric | Tags |
|-------|--------|------|
| Escalation created | `EscalationMetrics.Requests.Add(1)` | `EscalationConventions.AgentId`, `EscalationConventions.Priority`, `EscalationConventions.Strategy` |
| Escalation created | `EscalationMetrics.Pending.Add(1)` | (none) |
| Escalation resolved | `EscalationMetrics.Resolutions.Add(1)` | `EscalationConventions.ResolutionType`, `EscalationConventions.Priority` |
| Escalation resolved | `EscalationMetrics.DurationMs.Record(ms)` | `EscalationConventions.Priority` |
| Escalation resolved | `EscalationMetrics.Pending.Add(-1)` | (none) |
| Timeout fired | `EscalationMetrics.Timeouts.Add(1)` | `EscalationConventions.Priority` |
| Decision received | `EscalationMetrics.ApproverResponseMs.Record(ms)` | `EscalationConventions.ApproverName` |

### IDisposable

Dispose all `CancellationTokenSource` instances in the `ConcurrentDictionary` and clear the dictionary. Follow the `CapabilityMatchSupervisor.Dispose()` pattern:

```csharp
public void Dispose()
{
    foreach (var state in _activeEscalations.Values)
    {
        state.TimeoutCts.Cancel();
        state.TimeoutCts.Dispose();
    }
    _activeEscalations.Clear();
}
```

### Structured Logging

All operations log at appropriate levels using the `ILogger`:
- `LogInformation`: Escalation created, resolved
- `LogWarning`: Timeout fired, unknown escalation ID on decision submission
- `LogError`: Notification or audit store failures (caught, not thrown -- the escalation lifecycle continues)
- `LogDebug`: Strategy evaluation results, individual decision recording

Use `EscalationConventions` attribute names as structured log property names for correlation with OTel traces.

---

## DI Registration (deferred to section-19)

This class is registered as a singleton because it manages in-memory state:

```csharp
services.AddSingleton<IEscalationService, DefaultEscalationService>();
```

The singleton registration is critical -- multiple instances would create independent escalation state stores, causing missed decisions.

---

## Implementation Checklist

1. Create directory `src/Content/Infrastructure/Infrastructure.AI/Escalation/` (if not already created by section-09 or section-10)
2. Create `DefaultEscalationService.cs` with:
   - Private `EscalationState` nested class (Request, Decisions list, TCS, CTS, Lock, CreatedAt)
   - `ConcurrentDictionary<Guid, EscalationState>` field
   - Constructor injecting `IServiceProvider`, `IEscalationNotifier`, `IEscalationAuditStore`, `IOptionsMonitor<EscalationConfig>`, `ILogger<DefaultEscalationService>`
   - `RequestEscalationAsync` -- blocking flow with TCS await, timeout race, cancellation propagation
   - `QueueEscalationAsync` -- non-blocking flow returning escalation ID
   - `SubmitDecisionAsync` -- thread-safe decision evaluation with per-escalation lock
   - `GetPendingEscalationAsync` / `GetPendingEscalationsAsync` -- dictionary reads
   - Private `ResolveEscalation` -- audit, notify, metrics, complete TCS
   - Private `RunTimeoutAsync` -- `Task.Delay` race with timeout action dispatch
   - `IDisposable.Dispose` -- cancel and dispose all active CTS instances
3. Create test file `src/Content/Tests/Infrastructure.AI.Tests/Escalation/DefaultEscalationServiceTests.cs` with all test stubs listed above
4. Verify build: `dotnet build src/AgenticHarness.slnx`
5. Implement tests and verify: `dotnet test src/AgenticHarness.slnx --filter FullyQualifiedName~DefaultEscalationServiceTests`

---

## Conventions to Follow

- **Namespace:** `Infrastructure.AI.Escalation` -- matches `Infrastructure.AI.Agents`, `Infrastructure.AI.Audit`, etc.
- **XML docs:** Full `<summary>` on class and every public method. `<remarks>` explaining the in-memory limitation and Phase 3+ recovery plans.
- **Sealed class:** All Infrastructure implementations are sealed per codebase convention.
- **Immutability:** `EscalationState` uses `required init` for immutable fields, mutable `List<ApproverDecision>` protected by `lock`.
- **No `Task.Run` for timeout:** Use `_ = RunTimeoutAsync(state)` with a proper `async` method that catches its own exceptions. Never fire-and-forget without exception handling.
- **CancellationToken:** Every async method accepts and propagates `CancellationToken`. Linked tokens for caller cancellation.
- **Metric recording:** Use the static `EscalationMetrics` properties directly (they are thread-safe by design). Tag with `EscalationConventions` constants.
- **Error isolation:** Notification and audit failures are caught and logged, never propagated. The escalation lifecycle must not fail because Slack is down.

---

## Implementation Notes

**Status:** Complete
**Commit:** (see git log)

### Deviations from Plan
- Added `IsResolved` flag to `EscalationState` for consistent lock discipline across all three resolution paths (SubmitDecision, Timeout, Cancel). Prevents double-resolution race conditions.
- Converted `HandleTimeout` to `async Task HandleTimeoutAsync` — awaited by `RunTimeoutAsync` instead of fire-and-forget. Prevents silent exception swallowing if resolve fails before TrySetResult.
- `CleanupCancelledEscalation` now acquires lock, sets IsResolved, and calls TrySetCanceled on the TCS. Prevents timeout handler from racing in after caller disconnection.
- `Dispose` calls TrySetCanceled on all active TCS instances before Cancel+Dispose on CTS. Prevents callers from hanging on shutdown.
- Used `WaitAsync(ct)` instead of `CreateLinkedTokenSource` for cancellation propagation. Simpler, achieves the same result.
- Added `CancelEscalationAsync` implementation (on `IEscalationService` but not detailed in plan). Uses lock + IsResolved guard, throws if already resolved.

### Files Created
- `src/Content/Infrastructure/Infrastructure.AI/Escalation/DefaultEscalationService.cs` (~290 lines)
- `src/Content/Tests/Infrastructure.AI.Tests/Escalation/DefaultEscalationServiceTests.cs` (~340 lines)

### Test Results
- 15 tests, all passing
- Request lifecycle (3): creation+notification, blocking mode, audit recording
- Queue (1): non-blocking return, notification verified
- SubmitDecision (3): resolved outcome, partial decision, unknown ID
- Timeout (3): deny+escalate, caller cancellation, audit recording
- Concurrency (1): thread-safe, exactly one resolution
- CancelEscalation (2): resolves with denied, throws if already resolved
- GetPending (2): filters correctly, null after resolution

# Section 06: Escalation Interfaces

## Overview

This section defines the four core Application-layer interfaces for the human escalation subsystem. These interfaces live in `Application.AI.Common/Interfaces/Escalation/` and establish the contracts consumed by `DefaultEscalationService` (section-08), `JsonlEscalationAuditStore` (section-09), `CompositeEscalationNotifier` (section-10), and the governance pipeline integration (section-17).

All four interfaces depend exclusively on domain types from section-01 (`EscalationRequest`, `EscalationOutcome`, `ApproverDecision`, `EscalationAuditRecord`). No infrastructure, no third-party packages, no other Application-layer dependencies.

---

## Dependencies

| Dependency | Section | What It Provides |
|------------|---------|-----------------|
| Domain escalation models | section-01 | `EscalationRequest`, `EscalationOutcome`, `ApproverDecision`, `EscalationAuditRecord`, all enums |

**Blocked by this section:** section-08 (escalation service implementation), section-09 (audit store implementation), section-10 (notification adapter implementations).

---

## Files to Create

All files go in the same directory:

```
src/Content/Application/Application.AI.Common/Interfaces/Escalation/
    IEscalationService.cs
    IEscalationNotifier.cs
    IEscalationNotificationChannel.cs
    IEscalationAuditStore.cs
```

No existing files are modified.

---

## Tests

These are pure interface files with no logic. No unit tests are needed for the interfaces themselves. The interfaces are tested indirectly through their implementations:

- `DefaultEscalationServiceTests` (section-08 / section-21) tests `IEscalationService`
- `JsonlEscalationAuditStoreTests` (section-09 / section-21) tests `IEscalationAuditStore`
- `CompositeEscalationNotifierTests` (section-10 / section-21) tests `IEscalationNotifier` and `IEscalationNotificationChannel`

However, the DI registration tests from section-21 verify that these interfaces resolve correctly:

```csharp
// File: src/Content/Tests/Infrastructure.AI.Tests/DependencyInjection/EscalationDiTests.cs

// Test: AddEscalationServices_RegistersAllExpectedTypes
//   Arrange: Build a ServiceCollection with escalation service registrations
//   Act: Resolve IEscalationService, IEscalationNotifier, IEscalationAuditStore
//   Assert: All resolve to non-null instances of the expected concrete types

// Test: CompositeNotifier_DoesNotContainItself
//   Arrange: Register CompositeEscalationNotifier as IEscalationNotifier and IEscalationNotificationChannel entries
//   Act: Resolve IEscalationNotifier
//   Assert: The injected IEnumerable<IEscalationNotificationChannel> does NOT contain the composite itself
//           (regression guard against infinite recursion in the fan-out pattern)
```

These tests belong to section-19/section-21 but are noted here because they validate the interface/implementation separation is correctly wired.

---

## Interface Specifications

### 1. IEscalationService

The central orchestrator interface for the escalation lifecycle. Consumed by `GovernancePolicyBehavior` (when `Action == RequireApproval`) and by the supervisor (when `AutonomyExceeded`). Supports two consumption modes: blocking (caller awaits the outcome) and queue-and-continue (caller gets an ID and polls later).

**File:** `src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationService.cs`

**Namespace:** `Application.AI.Common.Interfaces.Escalation`

**Using:** `Domain.AI.Escalation`

**Contract:**

```csharp
/// <summary>
/// Orchestrates the escalation lifecycle: creation, notification dispatch,
/// approval tracking, timeout management, and outcome resolution.
/// </summary>
/// <remarks>
/// Two consumption modes are supported:
/// <list type="bullet">
///   <item><description><see cref="RequestEscalationAsync"/> -- blocking; caller awaits the human decision.</description></item>
///   <item><description><see cref="QueueEscalationAsync"/> -- non-blocking; returns an ID for later polling.</description></item>
/// </list>
/// The mode is selected by the agent's <c>EscalationWaitBehavior</c> (Block vs. QueueAndContinue),
/// resolved from the autonomy tier policy.
/// </remarks>
public interface IEscalationService
{
    /// <summary>
    /// Creates an escalation and blocks until a human decision resolves it or the timeout expires.
    /// </summary>
    Task<EscalationOutcome> RequestEscalationAsync(EscalationRequest request, CancellationToken ct);

    /// <summary>
    /// Creates an escalation without blocking. Returns the escalation ID for later polling.
    /// </summary>
    Task<Guid> QueueEscalationAsync(EscalationRequest request, CancellationToken ct);

    /// <summary>
    /// Submits an approver's decision. Returns the final outcome if this decision resolves
    /// the escalation (per the approval strategy), or null if the escalation is still pending.
    /// </summary>
    Task<EscalationOutcome?> SubmitDecisionAsync(Guid escalationId, ApproverDecision decision, CancellationToken ct);

    /// <summary>
    /// Returns the pending escalation request, or null if resolved or unknown.
    /// </summary>
    Task<EscalationRequest?> GetPendingEscalationAsync(Guid escalationId, CancellationToken ct);

    /// <summary>
    /// Returns all pending escalations assigned to a specific approver.
    /// </summary>
    Task<IReadOnlyList<EscalationRequest>> GetPendingEscalationsAsync(string approverName, CancellationToken ct);
}
```

**Design notes:**
- `RequestEscalationAsync` returns `Task<EscalationOutcome>` -- the implementation uses a `TaskCompletionSource<EscalationOutcome>` that completes when either a human decision resolves the escalation or the timeout fires. The caller's `CancellationToken` is linked to the escalation's internal `CancellationTokenSource` so caller disconnect cancels the escalation.
- `SubmitDecisionAsync` returns `EscalationOutcome?` rather than `bool` so the caller immediately gets the full outcome without a second round-trip. Null means "more decisions needed."
- `GetPendingEscalationsAsync` takes `approverName` (not approver ID) to match the `ApproverDecision.ApproverName` field from the domain model.

### 2. IEscalationNotifier

The public-facing notification contract consumed by `IEscalationService`. The implementation (`CompositeEscalationNotifier`) fans out to all registered `IEscalationNotificationChannel` instances. This two-interface split prevents infinite recursion: the composite implements `IEscalationNotifier` and injects `IEnumerable<IEscalationNotificationChannel>`, never receiving itself.

**File:** `src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationNotifier.cs`

**Namespace:** `Application.AI.Common.Interfaces.Escalation`

**Using:** `Domain.AI.Escalation`

**Contract:**

```csharp
/// <summary>
/// Delivers escalation notifications to human reviewers.
/// </summary>
/// <remarks>
/// The default implementation (<c>CompositeEscalationNotifier</c>) fans out to all
/// registered <see cref="IEscalationNotificationChannel"/> instances. Individual channel
/// failures are caught and logged without blocking other channels.
/// <para>
/// This interface is the public contract consumed by <c>IEscalationService</c>.
/// To add a new delivery channel, implement <see cref="IEscalationNotificationChannel"/>
/// and register it in DI -- do NOT implement this interface directly.
/// </para>
/// </remarks>
public interface IEscalationNotifier
{
    /// <summary>Notifies approvers that a new escalation requires their attention.</summary>
    Task NotifyEscalationRequestedAsync(EscalationRequest request, CancellationToken ct);

    /// <summary>Notifies interested parties that an escalation has been resolved.</summary>
    Task NotifyEscalationResolvedAsync(EscalationOutcome outcome, CancellationToken ct);

    /// <summary>Warns approvers that an escalation is about to expire.</summary>
    Task NotifyEscalationExpiringAsync(Guid escalationId, TimeSpan remaining, CancellationToken ct);
}
```

### 3. IEscalationNotificationChannel

The inner contract implemented by individual delivery adapters (AG-UI SSE, Slack, Teams, email, etc.). Each adapter implements this interface and is registered as `IEscalationNotificationChannel` in DI. The `CompositeEscalationNotifier` collects all registered channels via `IEnumerable<IEscalationNotificationChannel>`.

**File:** `src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationNotificationChannel.cs`

**Namespace:** `Application.AI.Common.Interfaces.Escalation`

**Using:** `Domain.AI.Escalation`

**Contract:**

```csharp
/// <summary>
/// A single delivery channel for escalation notifications (e.g., AG-UI, Slack, Teams).
/// </summary>
/// <remarks>
/// Implement this interface to add a new notification channel. Register the implementation
/// as <c>IEscalationNotificationChannel</c> in DI -- the <c>CompositeEscalationNotifier</c>
/// automatically discovers and fans out to all registered channels.
/// <para>
/// Implementations MUST be idempotent and MUST NOT throw exceptions that would block
/// other channels. The composite catches and logs per-channel failures.
/// </para>
/// </remarks>
public interface IEscalationNotificationChannel
{
    /// <summary>Notifies approvers that a new escalation requires their attention.</summary>
    Task NotifyEscalationRequestedAsync(EscalationRequest request, CancellationToken ct);

    /// <summary>Notifies interested parties that an escalation has been resolved.</summary>
    Task NotifyEscalationResolvedAsync(EscalationOutcome outcome, CancellationToken ct);

    /// <summary>Warns approvers that an escalation is about to expire.</summary>
    Task NotifyEscalationExpiringAsync(Guid escalationId, TimeSpan remaining, CancellationToken ct);
}
```

**Design note:** `IEscalationNotifier` and `IEscalationNotificationChannel` have identical method signatures by design. The separation is purely a DI concern: `IEscalationNotifier` has exactly one registration (the composite), while `IEscalationNotificationChannel` has N registrations (one per adapter). This prevents the composite from injecting itself.

### 4. IEscalationAuditStore

Append-only audit persistence for escalation events. Follows the same pattern as `IDelegationStore` -- JSONL append, no update/delete. Three record methods correspond to the three lifecycle events; `GetHistoryAsync` returns the full audit trail for a single escalation.

**File:** `src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationAuditStore.cs`

**Namespace:** `Application.AI.Common.Interfaces.Escalation`

**Using:** `Domain.AI.Escalation`

**Contract:**

```csharp
/// <summary>
/// Append-only audit store for escalation lifecycle events.
/// Records requests, individual approver decisions, and final outcomes
/// as <see cref="EscalationAuditRecord"/> entries for compliance.
/// </summary>
/// <remarks>
/// The default implementation writes JSONL (one JSON object per line) with file
/// locking, following the same pattern as <c>JsonlDelegationStore</c> from Phase 1.
/// Each record includes a <c>RecordType</c> discriminator for deserialization.
/// </remarks>
public interface IEscalationAuditStore
{
    /// <summary>Records that an escalation was created.</summary>
    Task RecordRequestAsync(EscalationRequest request, CancellationToken ct);

    /// <summary>Records an individual approver's decision.</summary>
    Task RecordDecisionAsync(Guid escalationId, ApproverDecision decision, CancellationToken ct);

    /// <summary>Records the final outcome of an escalation.</summary>
    Task RecordOutcomeAsync(EscalationOutcome outcome, CancellationToken ct);

    /// <summary>
    /// Returns the full audit history for a specific escalation, ordered chronologically.
    /// Returns an empty list if the escalation ID is unknown.
    /// </summary>
    Task<IReadOnlyList<EscalationAuditRecord>> GetHistoryAsync(Guid escalationId, CancellationToken ct);
}
```

---

## Implementation Checklist

1. Create directory `src/Content/Application/Application.AI.Common/Interfaces/Escalation/` (already existed from section-05)
2. Create `IEscalationService.cs` -- 6 async methods (added `CancelEscalationAsync` per code review)
3. Create `IEscalationNotifier.cs` -- 3 notification methods (requested, resolved, expiring) as the public composite contract
4. Create `IEscalationNotificationChannel.cs` -- same 3 method signatures as the per-adapter inner contract
5. Create `IEscalationAuditStore.cs` -- 3 write methods (request, decision, outcome) plus 1 read method (history by escalation ID)
6. Verify build: `dotnet build src/AgenticHarness.slnx` -- 0 errors

## Deviations from Plan

- **Added `CancelEscalationAsync`** to `IEscalationService` — Code review identified the need for explicit cancellation (agent disconnects, admin force-resolve, governance context changes). Returns `EscalationOutcome` for consistency.
- **Changed `NotifyEscalationExpiringAsync` signature** — Changed `Guid escalationId` parameter to `EscalationRequest request` on both `IEscalationNotifier` and `IEscalationNotificationChannel`. Provides full context to channel implementations, consistent with the other two notification methods.

---

## Conventions to Follow

These interfaces follow the established patterns visible in the codebase:

- **Namespace:** `Application.AI.Common.Interfaces.Escalation` -- matches `Interfaces/Governance/`, `Interfaces/Agents/`, etc.
- **XML docs:** Full `<summary>` on interface and every method. `<remarks>` on the interface explaining the implementation pattern and extension points. This is a template -- docs are teaching material.
- **Async suffix:** All methods are async and end with `Async`.
- **CancellationToken:** Every async method takes `CancellationToken ct` as the last parameter (not `ct = default` -- the caller is responsible for propagation).
- **Return types:** `Task<T>` for queries, `Task` for commands. Nullable return (`T?`) when the result may not exist.
- **Collections:** `IReadOnlyList<T>` for ordered collections on return types, matching the project convention.
- **No default implementations:** Interfaces contain zero logic. All behavior lives in the implementing classes.

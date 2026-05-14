# Code Review: Section 06 -- Escalation Interfaces

**Reviewer:** claude-code-reviewer
**Date:** 2026-05-09
**Scope:** 4 new files (pure interfaces, 131 lines total, no implementations, no tests)
**Verdict:** **Approve with warnings** -- no CRITICAL or HIGH issues. 3 MEDIUM items and 2 LOW suggestions.

---

## MEDIUM -- IEscalationService lacks a cancellation/withdrawal method

**File:** IEscalationService.cs

The interface covers create, submit decision, and query -- but no way to cancel a pending escalation. Section 17 (governance integration) shows the supervisor doing retry-after-approval, but there is no path for:

1. An agent cancelling its own escalation (e.g., the user cancelled the parent task)
2. An admin force-resolving a stuck escalation
3. The governance system revoking an escalation when context changes

**Downstream impact:** Section 08 (DefaultEscalationService) stores active escalations in a ConcurrentDictionary. Without a cancel method, entries can only exit via decision or timeout. If the caller CancellationToken fires during RequestEscalationAsync, the in-memory state leaks (the CTS is cleaned up via IDisposable, but the pending entry remains until timeout).

**Suggested addition:**

```csharp
/// <summary>
/// Cancels a pending escalation. Returns the outcome if the escalation was still pending,
/// or null if it was already resolved or unknown.
/// </summary>
Task<EscalationOutcome?> CancelEscalationAsync(
    Guid escalationId, string reason, CancellationToken ct);
```

**Recommendation:** Add this method. It is a natural lifecycle operation and prevents resource leaks in the in-memory implementation. Section 08 needs it regardless -- better to define it in the interface now than retrofit later.

---

## MEDIUM -- NotifyEscalationExpiringAsync passes raw data without full context

**Files:** IEscalationNotificationChannel.cs:26, IEscalationNotifier.cs:27

```csharp
Task NotifyEscalationExpiringAsync(
    Guid escalationId, TimeSpan remaining, CancellationToken ct);
```

The escalationId + remaining pair requires the channel implementation to resolve the full EscalationRequest from the service or store to include meaningful context in the notification (agent name, tool name, approvers). The other two methods pass the full domain objects (EscalationRequest, EscalationOutcome).

**Suggestion:** Pass the full EscalationRequest instead of just the Guid:

```csharp
Task NotifyEscalationExpiringAsync(
    EscalationRequest request, TimeSpan remaining, CancellationToken ct);
```

This makes channel implementations self-contained -- no back-call to the service needed. Section 08 already holds the request in memory, so this is zero-cost to provide.

**Recommendation:** Change now before implementations exist. Keeps notification channels pure (receive context, deliver notification) without needing service dependencies.

---

## MEDIUM -- Identical method signatures across IEscalationNotifier and IEscalationNotificationChannel

**Files:** IEscalationNotifier.cs, IEscalationNotificationChannel.cs

Both interfaces define the exact same 3 methods with the exact same signatures. The design intent is correct -- IEscalationNotifier is the public contract (consumed by IEscalationService), and IEscalationNotificationChannel is the per-adapter inner contract (consumed by CompositeEscalationNotifier). The composite pattern requires this 1:1 mapping.

**Why this is still a warning:** Two interfaces with identical signatures but different semantic roles is a common source of confusion for template consumers.

**Counter-argument:** The current design gives freedom to evolve the interfaces independently. If IEscalationNotifier later gains aggregate methods (e.g., NotifyBatchAsync) that channels should not implement, inheritance would be wrong. The XML docs already explain the pattern clearly with remarks sections.

**Recommendation:** Keep as-is. The docs are sufficient, and independent evolution is the more likely future. Informational only -- no change needed.

---

## LOW -- IEscalationAuditStore.GetHistoryAsync lacks pagination

**File:** IEscalationAuditStore.cs:30

```csharp
Task<IReadOnlyList<EscalationAuditRecord>> GetHistoryAsync(
    Guid escalationId, CancellationToken ct);
```

Compare with IObservabilityStore.GetSessionsAsync which includes limit, offset, since, until parameters. For this audit store, a single escalation should have a small number of records (1 request + N decisions + 1 outcome), so pagination is not critical.

**Recommendation:** Acceptable for now. The interface is scoped to per-escalation history. If cross-escalation audit queries are needed later, they belong on a separate IEscalationAuditQueryService interface rather than bloating this one.

---

## LOW -- CancellationToken parameter named ct vs codebase convention

**Files:** All 4 interface files

All methods use ct as the CancellationToken parameter name. The existing codebase is split:
- ct: MCP interfaces (IMcpResourceProvider, IMcpPromptProvider)
- cancellationToken: agent/observability interfaces (IObservabilityStore, IAuditSink, ITextContentSafetyService)

The newer MCP interfaces use ct, so these escalation interfaces are consistent with the more recent additions.

**Recommendation:** No change needed. The ct convention aligns with the direction of travel for newer interfaces.

---

## Positive Observations

1. **Namespace placement is correct.** All interfaces in Application.AI.Common.Interfaces.Escalation, consistent with Clean Architecture rules and existing interface organization.

2. **XML docs are excellent teaching material.** The remarks sections on IEscalationNotifier and IEscalationNotificationChannel clearly explain the composite pattern relationship, DI registration pattern, and the idempotency/error-handling contract. This is exactly what template consumers need.

3. **Return types use immutability correctly.** IReadOnlyList on GetHistoryAsync and GetPendingEscalationsAsync. Nullable return types (EscalationOutcome?, EscalationRequest?) on query methods that may return nothing.

4. **Separation of concerns is clean.** Four interfaces with clear single-responsibility boundaries: orchestration, public notification, per-channel notification, and audit persistence.

5. **Domain model alignment is tight.** Every parameter and return type maps directly to the section-01 domain records. No interface-specific DTOs or unnecessary mapping layers.

6. **Dual consumption modes on IEscalationService** (RequestEscalationAsync blocking vs QueueEscalationAsync non-blocking) are well-documented and map cleanly to the EscalationWaitBehavior enum from section 01.

---

## Summary

| Priority | Count | Action |
|----------|-------|--------|
| CRITICAL | 0 | -- |
| HIGH | 0 | -- |
| MEDIUM | 3 | Add CancelEscalationAsync to IEscalationService; change NotifyEscalationExpiringAsync to pass EscalationRequest instead of Guid; dual-interface pattern is fine (informational) |
| LOW | 2 | Pagination acceptable as-is; ct naming aligns with newer conventions |

**Verdict: Approve with warnings.** The CancelEscalationAsync addition and NotifyEscalationExpiringAsync signature change are recommended before section 08 implementation begins, as they are easier to add to an interface than to retrofit through implementations.

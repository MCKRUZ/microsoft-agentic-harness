# Code Review: Section 08 -- Escalation Service Implementation

**Reviewer:** claude-code-reviewer
**Date:** 2026-05-09
**Scope:** 2 new files (DefaultEscalationService.cs: 376 lines, DefaultEscalationServiceTests.cs: 368 lines)
**Verdict:** **Block** -- 1 CRITICAL race condition, 2 HIGH issues requiring fixes before merge.

---

## CRITICAL -- CancelEscalationAsync has no lock guard against concurrent resolution

**File:** DefaultEscalationService.cs:162-180

CancelEscalationAsync reads state.Decisions and calls ResolveEscalationAsync without acquiring state.Lock or checking state.IsResolved. This creates a race condition:

1. Thread A: SubmitDecisionAsync acquires lock(state.Lock), sets state.IsResolved = true, releases lock.
2. Thread B: CancelEscalationAsync runs concurrently -- it never checks IsResolved and never acquires the lock.
3. Thread B reads state.Decisions.ToList() while Thread A may still be inside ResolveEscalationAsync (after releasing the lock but before TrySetResult completes).
4. ResolveEscalationAsync is called twice. The TrySetResult guard prevents double-completion of the TCS, but the audit store and notifier get called twice (double audit records, double notifications).

Compare with HandleTimeout (lines 275-305) which correctly acquires lock(state.Lock) and checks state.IsResolved. CancelEscalationAsync must follow the same pattern.

**Fix:** Wrap decision-reading and outcome-building in lock(state.Lock) with an IsResolved guard. Add test: CancelEscalationAsync_AlreadyResolved_ThrowsInvalidOperation.

---

## HIGH -- CleanupCancelledEscalation does not set IsResolved, allowing double resolution

**File:** DefaultEscalationService.cs:307-316

CleanupCancelledEscalation removes state from _activeEscalations and cancels timeout CTS, but never sets state.IsResolved = true and never acquires state.Lock.

Race: caller cancellation and Task.Delay completion can both proceed. HandleTimeout checks state.IsResolved (still false), sets it true, calls ResolveEscalationAsync. The TCS may get TrySetResult instead of TrySetCanceled, violating the caller cancellation contract. Even without the race, the TCS is left dangling.

**Fix:** Add lock(state.Lock) with IsResolved guard, then TrySetCanceled() before cancelling the timeout CTS.

---

## HIGH -- HandleTimeout fire-and-forgets ResolveEscalationAsync

**File:** DefaultEscalationService.cs:304

Uses _ = ResolveEscalationAsync(state, outcome) which discards the task. If ResolveEscalationAsync throws before TrySetResult, the TCS is never completed and the caller hangs forever.

**Fix:** Make HandleTimeout async, await ResolveEscalationAsync. The outer try/catch in RunTimeoutAsync handles exceptions.
---

## MEDIUM -- Missing linked CancellationToken per section plan

**File:** DefaultEscalationService.cs:63-79

The section plan specifies linking the caller CT via CreateLinkedTokenSource(). The implementation passes ct directly to WaitAsync(ct). When the caller disconnects, Task.Delay continues until timeout (potentially 300s). The pattern reference (CapabilityMatchSupervisor.cs:240) uses CreateLinkedTokenSource.

**Recommendation:** Link the caller CT into the timeout CTS.

---

## MEDIUM -- Dispose does not complete pending TCS instances

**File:** DefaultEscalationService.cs:183-191

Dispose cancels CTS but never completes TaskCompletionSource. Callers blocked on RequestEscalationAsync hang forever. The TCS is orphaned because HandleTimeout catches OperationCanceledException without completing the TCS.

**Fix:** Call state.Completion.TrySetCanceled() before cancelling the CTS.

---

## MEDIUM -- Decisions list read outside lock in CancelEscalationAsync

**File:** DefaultEscalationService.cs:172

state.Decisions is mutable List. .ToList() in CancelEscalationAsync without lock vs .Add() in SubmitDecisionAsync under lock = potential InvalidOperationException. Resolved by CRITICAL fix (lock wraps .ToList()).

---

## MEDIUM -- No test for CancelEscalationAsync

**File:** DefaultEscalationServiceTests.cs (entire file)

Zero tests for CancelEscalationAsync. Required: CancelEscalationAsync_PendingEscalation_ReturnsDeniedOutcome, CancelEscalationAsync_UnknownId_ThrowsInvalidOperation, CancelEscalationAsync_AlreadyResolved_ThrowsInvalidOperation.

---

## MEDIUM -- Concurrency test missing audit assertion

**File:** DefaultEscalationServiceTests.cs:701-719

ConcurrentDecisions test asserts one non-null outcome but does not verify _auditStore.RecordOutcomeAsync called exactly once. Audit assertion is the stronger race-condition invariant.

**Fix:** Add _auditStore.Verify(a => a.RecordOutcomeAsync(...), Times.Once).

---

## MEDIUM -- Missing metrics test coverage

**File:** DefaultEscalationServiceTests.cs (entire file)

Section plan specifies RequestEscalationAsync_IncrementsRequestsCounter and Resolution_RecordsDurationHistogram. Neither implemented. Track as known gap.

---

## LOW -- EscalationState.IsResolved should be volatile or guarded comment

**File:** DefaultEscalationService.cs:379

IsResolved is plain bool read inside lock blocks. Adding a comment documenting the lock-guard requirement would prevent regressions. Defensive suggestion, not a bug.

---

## LOW -- Stopwatch-based timing in QueueEscalationAsync test

**File:** DefaultEscalationServiceTests.cs:586-595

1000ms bound is safe. Stopwatch assertions on async code are fragile under CI load. No change needed.

---

## LOW -- Case-sensitive string equality in GetPendingEscalationsAsync

**File:** DefaultEscalationService.cs:155

Contains uses ordinal comparison. Document the case-sensitivity contract.

---

## Summary

| Severity | Count | Items |
|----------|-------|-------|
| CRITICAL | 1 | CancelEscalationAsync missing lock guard (double audit/notification race) |
| HIGH | 2 | CleanupCancelledEscalation missing IsResolved + TCS completion; HandleTimeout fire-and-forget leak |
| MEDIUM | 5 | Missing linked CTS; Dispose orphans TCS; No CancelEscalation tests; Missing audit assertion; Missing metrics tests |
| LOW | 3 | Volatile suggestion; Stopwatch brittleness; String case sensitivity |

**Verdict: Block.** Fix the CRITICAL race condition in CancelEscalationAsync, the HIGH cleanup/timeout issues, and add CancelEscalation test coverage before merging.

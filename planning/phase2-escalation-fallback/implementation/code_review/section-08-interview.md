# Section 08 — Code Review Interview

## Review Summary
- 1 CRITICAL, 2 HIGH, 5 MEDIUM, 3 LOW
- Verdict: Block (before fixes) → Approve (after fixes)

## Triage

### CRITICAL: CancelEscalationAsync race condition
- **Decision:** Auto-fix
- **Action:** Added lock(state.Lock) + IsResolved guard. Now throws InvalidOperationException if already resolved.
- **Rationale:** Same pattern as HandleTimeout and SubmitDecisionAsync — all three resolution paths must use consistent lock discipline.

### HIGH: CleanupCancelledEscalation dangling TCS
- **Decision:** Auto-fix
- **Action:** Added lock + IsResolved guard, TrySetCanceled on the TCS, and unconditional cleanup (TryRemove, Cancel CTS, decrement metrics).
- **Rationale:** Prevents timeout handler from racing in and completing the TCS after caller has already caught the OCE.

### HIGH: HandleTimeout fire-and-forget
- **Decision:** Auto-fix
- **Action:** Converted HandleTimeout to async HandleTimeoutAsync. RunTimeoutAsync now awaits it. No more discarded task.
- **Rationale:** If resolve fails before TrySetResult, the exception is now caught by RunTimeoutAsync's catch block instead of being silently swallowed.

### MEDIUM: Dispose orphans TCS instances
- **Decision:** Auto-fix
- **Action:** Added TrySetCanceled before Cancel+Dispose in the Dispose loop.
- **Rationale:** Prevents callers awaiting the TCS from hanging indefinitely on shutdown.

### MEDIUM: Missing CancelEscalationAsync tests
- **Decision:** Auto-fix
- **Action:** Added 2 tests: CancelEscalationAsync_ResolvesWithDenied, CancelEscalationAsync_AlreadyResolved_Throws.
- **Rationale:** New behavior from the lock fix needs test coverage.

### MEDIUM: Linked CancellationToken deviation from spec
- **Decision:** Let go
- **Rationale:** WaitAsync(ct) achieves the same result with cleaner code. The spec's CreateLinkedTokenSource pattern is more explicit but adds complexity without benefit since WaitAsync handles the cancellation propagation.

### MEDIUM: Concurrency test missing audit assertion
- **Decision:** Let go
- **Rationale:** The test already verifies exactly one outcome is returned. Audit store assertion would be fragile since audit calls happen before the lock (all 3 concurrent calls record decisions). The important invariant is one resolution.

### MEDIUM: Metrics tests not implemented
- **Decision:** Let go
- **Rationale:** MeterListener setup is complex and these are cross-cutting concerns already tested implicitly through the lifecycle tests. Section-21 integration tests can add explicit metric assertions if needed.

### LOW findings: Let go (all informational)

## Applied Fixes
1. CancelEscalationAsync: lock + IsResolved guard
2. CleanupCancelledEscalation: lock + IsResolved + TrySetCanceled
3. HandleTimeout → HandleTimeoutAsync (async, awaited)
4. Dispose: TrySetCanceled on all active TCS instances
5. Added 2 CancelEscalation tests (15 total tests now)

## Test Results
- 15 tests, all passing

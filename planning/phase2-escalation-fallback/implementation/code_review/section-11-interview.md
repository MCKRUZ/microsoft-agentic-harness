# Section 11: Code Review Interview

## Triage Summary

| # | Finding | Disposition |
|---|---------|-------------|
| 1 | MaxAttempts semantic mismatch (HIGH) | Auto-fix — applied `MaxAttempts - 1` conversion |
| 2 | Shared CircuitBreakerStateProvider illusion (HIGH) | Asked user → Document as independent |
| 3 | ShouldHandle misses Azure.RequestFailedException (MED) | Let go — future work when SDKs integrated |
| 4 | ParseBackoffType silent default (MED) | Auto-fix — now throws on unknown values |
| 5 | No tests for BuildForStreamInitiation (MED) | Let go — section-12 will exercise |
| 6 | Missing TransitionFrom tag (MED) | Auto-fix — added to both RecordCircuit* methods |
| 7 | Unused using System.Diagnostics (LOW) | Auto-fix — removed |
| 8-10 | Minor items (LOW) | Let go |

## Interview

**Q: BuildForStreamInitiation claims shared CircuitBreakerStateProvider but Polly v8 gives each pipeline independent circuit state. Remove the illusion or remove the method?**
A: User chose "Document as independent" — keep both methods, update docs to state circuits are independent. Health monitor reads from typed pipeline's state only.

## Applied Fixes

1. `MaxRetryAttempts = Math.Max(1, retryConfig.MaxAttempts - 1)` — converts "total attempts" to "retry count"
2. Removed `using System.Diagnostics` (unused)
3. `ParseBackoffType` now uses switch expression and throws on unknown values
4. Added `TransitionFrom` tag to `RecordCircuitOpened` and `RecordCircuitClosed`
5. Updated BuildForStreamInitiation XML docs to clarify independent circuit state
6. Updated test maxAttempts values to match new semantics

All 8 tests pass after fixes.

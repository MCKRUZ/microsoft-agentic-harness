# Section 11: Polly Pipelines — Code Review

## Verdict: WARNING (2 HIGH issues)

### HIGH

1. **MaxAttempts semantic mismatch** — RetryConfig.MaxAttempts doc says "including initial attempt" but code passes directly to Polly's MaxRetryAttempts (retries after initial). Config value 2 yields 3 total calls.
2. **CircuitBreakerStateProvider sharing illusion** — BuildForStreamInitiation assigns same StateProvider but Polly v8 binds it per-pipeline. May not actually share circuit state.

### MEDIUM

3. ShouldHandle misses Azure.RequestFailedException — HttpRequestException only catches raw HTTP, not SDK-wrapped exceptions.
4. ParseBackoffType silently defaults to Exponential on typos.
5. No tests for BuildForStreamInitiation.
6. Missing TransitionFrom tag on CircuitStateChanges metric.

### LOW

7. Unused `using System.Diagnostics`
8. async lambda without await generates CS1998
9. Test BaseDelaySeconds of 0.01 may cause CI flakiness
10. out parameter vs tuple/record style

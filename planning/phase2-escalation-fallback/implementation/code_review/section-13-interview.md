# Section 13: Code Review Interview

## Triage Summary

| # | Finding | Disposition |
|---|---------|-------------|
| 1 | TOCTOU race in ReportStateChange (HIGH) | Auto-fix — used AddOrUpdate for atomic transition |
| 2 | Event invocation thread safety (HIGH) | Auto-fix — capture delegate to local |
| 3 | UpDownCounter semantics (MED) | Let go — enum ordering documented as contractual |
| 4 | IsAnyProviderHealthy excludes Degraded (MED) | Let go — matches spec "at least one Healthy" |
| 5 | Missing concurrent test (MED) | Auto-fix — added Parallel.For test |
| 6 | Test name mismatch (MED) | Auto-fix — renamed to match behavior |
| 7-8 | Low items | Let go |

## Applied Fixes

1. Replaced GetOrAdd + indexer with `AddOrUpdate` pattern — atomic read+compare+write, captures oldState via closure
2. Captured `OnCircuitStateChanged` delegate to local before invocation
3. Added `ReportStateChange_ConcurrentCalls_FiresEventExactlyOnce` test using Parallel.For
4. Renamed first test to `GetProviderHealth_ExplicitlyReportedHealthy_ReturnsHealthy`

All 11 tests pass after fixes (including concurrency test).

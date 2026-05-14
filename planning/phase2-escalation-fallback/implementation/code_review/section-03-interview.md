# Section 03 — Code Review Interview

## Triage

### Auto-fix (applied)
- Added `<remarks>` to `ResilienceMetrics.CircuitState` documenting the delta-recording requirement for UpDownCounter-as-gauge semantics. Section 13 implementors need to know they must track previous state and record the difference.

### Let go
- Test style inconsistency: New tests use stricter `.Name.Should().Be(ConventionConstant)` vs older `NotBeNullOrWhiteSpace()`. Positive evolution — backfill older tests in future cleanup.
- Unit string convention: New files correctly use bare `"ms"`, older OrchestrationMetrics uses `"{ms}"`. Out of scope.

## Interview
No items required user input — all findings were either obvious auto-fixes or informational observations.

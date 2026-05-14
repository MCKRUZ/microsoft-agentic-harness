# Code Review: Section 03 — OTel Conventions & Metrics

**Verdict:** APPROVE

## Findings

### [MEDIUM] CircuitState UpDownCounter — add delta-recording remark
`ResilienceMetrics.CircuitState` uses UpDownCounter for gauge semantics (0/1/2). Recording sites must track previous state and record the delta. Suggest adding `<remarks>` to document this requirement for Section 13 implementors.

### [MEDIUM] Test style evolution (informational, no action)
New tests assert `.Name.Should().Be(ConventionConstant)` — stricter than older `NotBeNullOrWhiteSpace()` tests. Positive evolution, no action needed now. Consider backfilling older tests in future cleanup.

### [LOW] Unit string convention (informational)
New files correctly use bare `"ms"` for duration histograms (OTel standard). Older `OrchestrationMetrics` uses `"{ms}"` inconsistently. Not in scope.

## Summary
Clean, consistent code. All instrument types correct. No security issues. Full XML docs. 16 tests covering all public properties.

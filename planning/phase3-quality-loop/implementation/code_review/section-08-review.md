# Section 08 -- Drift Detection Service + Notifier: Code Review

**Reviewer:** claude-code-reviewer
**Scope:** DriftConventions, DriftMetrics, CompositeDriftNotifier, DefaultDriftDetectionService + 3 test files (20 tests)
**Verdict:** APPROVE with HIGH and MEDIUM items noted

---

## Critical Issues

None.

---

## High Issues

### [HIGH] GetDriftHistoryAsync loads ALL graph nodes into memory

**File:** Infrastructure.AI/DriftDetection/DefaultDriftDetectionService.cs:216

**Issue:** GetAllNodesAsync() fetches every node in the knowledge graph -- not just DriftEvent nodes. In a production graph with thousands of entity nodes, this loads the entire graph into memory just to filter for drift events. The subsequent LINQ pipeline runs in-memory on the full set. This is called by UpdateBaselineAsync on every baseline recalculation.

**Fix:** Add a typed query method to IKnowledgeGraphStore (e.g., GetNodesByTypeAsync) or use QueryNodesAsync if available. If out of scope for this section, add a PERF comment documenting the known O(N) scan.

**Severity rationale:** HIGH because this method is on the critical path for baseline updates, and graph size grows unboundedly in production.

---

### [HIGH] EvaluationDurationMs histogram is declared but never recorded

**File:** Application.AI.Common/OpenTelemetry/Metrics/DriftMetrics.cs:31-32 and DefaultDriftDetectionService.cs (entire file)

**Issue:** DriftMetrics.EvaluationDurationMs histogram is created but EvaluateDriftAsync never records a measurement to it. The method creates a timestamp from TimeProvider but only uses it for scoring timestamps, never for elapsed time. The one metric that would help diagnose latency problems is dead code.

**Fix:** Add a Stopwatch.StartNew() or TimeProvider.GetTimestamp() pair around the evaluation pipeline and record the elapsed duration.

**Severity rationale:** HIGH because declaring observability instruments without wiring them is misleading -- dashboards show the metric with zero data.

---

## Medium Issues

### [MEDIUM] Fallback uses same scopeIdentifier across different scopes

**File:** Infrastructure.AI/DriftDetection/DefaultDriftDetectionService.cs:241-256

**Issue:** When falling back from TaskType to Skill to Agent, the same scopeIdentifier is passed to every scope level. This assumes the identifier is meaningful across scope hierarchies. A task-type identifier like pr-42-review would not match any Skill or Agent baseline. The fallback silently returns null, which is correct, but the assumption is undocumented.

**Recommendation:** Document this assumption in the XML doc or DriftEvaluationRequest model.

---

### [MEDIUM] DeserializeDriftScore swallows all exceptions silently

**File:** Infrastructure.AI/DriftDetection/DefaultDriftDetectionService.cs:338-362

**Issue:** The bare catch block swallows every exception type. When a graph node has corrupted properties, the deserialization silently returns null. Over time, drift history queries may return fewer results than expected with no diagnostic trail.

**Fix:** Catch Exception ex, log at Debug or Warning level with the node ID. Requires making the method non-static or passing an ILogger parameter.

---

### [MEDIUM] BaselineId is lost during graph round-trip

**File:** Infrastructure.AI/DriftDetection/DefaultDriftDetectionService.cs:349

**Issue:** BaselineId is not serialized into the graph node Properties dictionary in PersistDriftEventAsync. When deserializing in DeserializeDriftScore, it is hardcoded to Guid.Empty. GetDriftHistoryAsync returns scores that have lost their baseline correlation.

**Fix:** Add BaselineId to the Properties dictionary in PersistDriftEventAsync, parse it back in DeserializeDriftScore.

---

### [MEDIUM] No test coverage for UpdateBaselineAsync or GetDriftHistoryAsync

**File:** Infrastructure.AI.Tests/DriftDetection/DefaultDriftDetectionServiceTests.cs

**Issue:** 12 tests for EvaluateDriftAsync, 1 for GetBaselineAsync, zero for UpdateBaselineAsync (70 lines of untested logic including statistical computation) and zero for GetDriftHistoryAsync (deserialization from graph nodes).

**Recommendation:** Add at minimum:
1. UpdateBaseline_SufficientSamples_ComputesBaselineCorrectly
2. UpdateBaseline_InsufficientSamples_ReturnsFailure
3. UpdateBaseline_Disabled_ReturnsFailure
4. GetDriftHistory_FiltersCorrectly
5. GetDriftHistory_MalformedNode_SkipsGracefully

---

### [MEDIUM] No test for all-dimensions-fail-scoring path

**File:** Infrastructure.AI.Tests/DriftDetection/DefaultDriftDetectionServiceTests.cs

**Issue:** EvaluateDriftAsync has a guard returning failure when dimensionScores.Count == 0. No test covers this path. Distinct failure mode from no-baseline.

---

### [MEDIUM] No test for TaskType scope fallback (full chain)

**File:** Infrastructure.AI.Tests/DriftDetection/DefaultDriftDetectionServiceTests.cs

**Issue:** Fallback test only covers Skill-to-Agent. No test for TaskType-to-Skill-to-Agent (full 3-level chain). Since FallbackOrder starts at requested scope index, a TaskType request tries all three. Should be explicitly tested.

---

## Suggestions

### [LOW] Population variance vs. sample variance in UpdateBaselineAsync

**File:** Infrastructure.AI/DriftDetection/DefaultDriftDetectionService.cs:183

**Issue:** Variance divides by N (population). For sample baseline estimation, Bessel correction (N-1) is statistically correct. With MinSamplesForBaseline=20, practical difference is ~5%, but systematically underestimates sigma making thresholds too sensitive.

**Recommendation:** Use (values.Count - 1) as denominator for sample variance.

---

### [LOW] DriftConventions.ScopeValues could drift from DriftScope enum

**File:** Domain.AI/Telemetry/Conventions/DriftConventions.cs:30-35

**Issue:** ScopeValues strings are manually maintained. The service uses Scope.ToString().ToLowerInvariant() for metric tags. If enum values are renamed, they diverge silently.

**Recommendation:** Derive ScopeValues from the enum or use constants when emitting metrics.

---

## Security

- No hardcoded credentials, API keys, or tokens.
- No SQL injection risk (no raw SQL).
- No user-controlled file paths.
- JsonSerializer is safe against injection (System.Text.Json).
- Graph node properties are all string-typed, no executable content.

---

## Structural Quality

- **CompositeDriftNotifier** is a clean mirror of CompositeEscalationNotifier -- consistent pattern.
- **DefaultDriftDetectionService** at 363 lines is within the 400-line target. Clean separation across private methods.
- **DriftConventions** and **DriftMetrics** are appropriately thin.
- **Immutability**: All dictionaries use .AsReadOnly(), records with required init properties. Correct.
- **Error isolation**: SafeExecuteAsync wraps audit, graph persistence, and notification. Escalation also wrapped. Main pipeline (baseline resolution, scoring) correctly propagates failures.
- **Thread safety**: Service is stateless. FallbackOrder is static readonly. DriftMetrics counters/histograms are thread-safe by design. No concurrency issues.

---

## Summary

| Priority | Count | Items |
|----------|-------|-------|
| CRITICAL | 0 | -- |
| HIGH | 2 | GetAllNodesAsync full scan, EvaluationDurationMs never recorded |
| MEDIUM | 5 | Fallback scopeIdentifier assumption, silent catch, BaselineId lost, missing UpdateBaseline/GetDriftHistory tests, missing all-dimensions-fail test |
| LOW | 2 | Population vs. sample variance, ScopeValues string drift |

**Verdict: APPROVE** -- The HIGH items are real but non-blocking: the graph scan is a known limitation of the current IKnowledgeGraphStore interface (not solvable in this section alone), and the missing histogram recording is a quick fix. The MEDIUM test coverage gaps should be addressed before the PR.

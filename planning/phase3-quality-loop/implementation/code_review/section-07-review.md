# Section 07 -- EWMA Drift Scorer: Code Review

**Reviewer:** claude-code-reviewer
**Scope:** EwmaDriftScorer, DriftSeverityClassifier, GraphEwmaStateStore + 3 test files
**Verdict:** APPROVE with MEDIUM items noted

---

## Findings

### [MEDIUM] Negative deviation input not handled in DriftSeverityClassifier

**File:** Infrastructure.AI/DriftDetection/DriftSeverityClassifier.cs:24
**Issue:** The XML doc says deviation is non-negative, but nothing enforces it. A negative deviation (which should not happen given Math.Abs in the scorer) would silently return DriftSeverity.None. As a public static method on a template project, consumers could call it directly with invalid input.
**Recommendation:** Either add a guard clause (ArgumentOutOfRangeException) or document that negative values are treated as None. Since the only caller (EwmaDriftScorer) guarantees non-negative via Math.Abs, this is defensive rather than critical.
**Severity rationale:** MEDIUM because it is a pure function with a single caller that already guarantees the invariant.

---

### [MEDIUM] scopeIdentifier containing colons would create ambiguous deterministic IDs

**File:** Infrastructure.AI/DriftDetection/GraphEwmaStateStore.cs:236 and Application.AI.Common/Interfaces/DriftDetection/EwmaState.cs:32
**Issue:** BuildId and EwmaState.DeterministicId both use colon-delimited format ewma:{scope}:{scopeIdentifier}:{dimension}. If scopeIdentifier contains colons, the ID becomes unparseable. While no current callers pass colon-containing identifiers, this is a template project where consumers control the input.
**Recommendation:** Either (1) validate scopeIdentifier excludes colons in validators, or (2) use a non-ambiguous separator. Option 1 is simpler and consistent with existing baseline validators.

---

### [MEDIUM] GetStatesAsync issues sequential GetNodeAsync calls -- N+1 pattern

**File:** Infrastructure.AI/DriftDetection/GraphEwmaStateStore.cs:218-225
**Issue:** Iterates all DriftDimension enum values and calls _graphStore.GetNodeAsync one at a time. With 6 dimensions, this is 6 sequential round-trips. Not a performance problem with in-memory stores, but becomes one with networked backends (Neo4j, PostgreSQL).
**Recommendation:** Use Task.WhenAll for parallelism. Materialize tasks list, await all, then iterate completed results.
**Severity rationale:** MEDIUM because current enum has 6 values and in-memory store is the primary backend. Flag for when production graph backend lands.

---

### [MEDIUM] DeserializeState throws raw exceptions on malformed graph data

**File:** Infrastructure.AI/DriftDetection/GraphEwmaStateStore.cs:255-263
**Issue:** Enum.Parse, double.Parse, int.Parse, and DateTimeOffset.Parse will throw on corrupt/missing properties. The caller (GetStateAsync) catches Exception broadly, which converts to Result.Fail, so it will not crash. But the error message exposes raw exception text (ex.Message) which may leak internal state format details.
**Recommendation:** Acceptable given the catch-all in GetStateAsync. For tighter error messages, wrap DeserializeState in a dedicated try-catch. Current design is fine as-is.

---

### [LOW] DriftSeverityClassifier has no null check on config parameter

**File:** Infrastructure.AI/DriftDetection/DriftSeverityClassifier.cs:24
**Issue:** Passing null config throws NullReferenceException rather than ArgumentNullException. Minor since the only caller pulls config from IOptionsMonitor (never null), but as a public static method on a template, explicit validation is better teaching material.
**Fix:** Add ArgumentNullException.ThrowIfNull(config) at method entry.

---

### [LOW] Test coverage gap -- missing test for stateStore.GetStateAsync returning failure

**File:** Infrastructure.AI.Tests/DriftDetection/EwmaDriftScorerTests.cs
**Issue:** No test for when _stateStore.GetStateAsync returns a failure Result. The code at line 97-98 handles this path but it is untested.
**Fix:** Add a test that mocks GetStateAsync to return Result.Fail and asserts the scorer propagates the failure.

---

### [LOW] Test coverage gap -- missing test for stateStore.SaveStateAsync returning failure

**File:** Infrastructure.AI.Tests/DriftDetection/EwmaDriftScorerTests.cs
**Issue:** Same pattern. The code at line 119-121 handles save failure, but no test exercises it.
**Fix:** Similar to above but mock SaveStateAsync to return failure.

---

### [LOW] Test coverage gap -- GraphEwmaStateStore exception paths untested

**File:** Infrastructure.AI.Tests/DriftDetection/GraphEwmaStateStoreTests.cs
**Issue:** GetStateAsync has a try-catch that logs and returns Result.Fail. No test verifies this path. Same for SaveStateAsync and GetStatesAsync exception paths.

---

### [INFO] EWMA math is correct

**Verification:** Formula EWMA_t = lambda * x_t + (1 - lambda) * EWMA_{t-1} matches the textbook EWMA definition. The multi-step test independently verifies the recurrence over 5 observations. First evaluation correctly seeds from baseline mean. Zero-variance guard prevents division by zero. Deviation formula |EWMA - baseline_mean| / sigma is the standard sigma-units computation for EWMA control charts. All verified.

---

### [INFO] Thread safety is acceptable

**Analysis:** EwmaDriftScorer holds no mutable state. It reads config via IOptionsMonitor.CurrentValue (thread-safe), delegates persistence to IEwmaStateStore, and uses TimeProvider (thread-safe). GraphEwmaStateStore holds no mutable state. Thread safety depends on the IKnowledgeGraphStore implementation, which is the correct place to enforce it. DriftSeverityClassifier is a static pure function. All safe for singleton registration.

---

### [INFO] Clean Architecture compliance is correct

- EwmaDriftScorer depends on Application.AI.Common.Interfaces (IDriftScorer, IEwmaStateStore) and Domain.AI (DriftDimension, DriftBaseline) and Domain.Common (Result, AppConfig). All dependencies point inward.
- GraphEwmaStateStore depends on Application.AI.Common.Interfaces.KnowledgeGraph (IKnowledgeGraphStore) and Domain.AI.KnowledgeGraph.Models (GraphNode). Correct: Infrastructure implementing Application interfaces using Domain models.
- DriftSeverityClassifier depends only on Domain types. Fine in Infrastructure since it is a utility consumed by Infrastructure orchestration code.

---

### [INFO] Serialization is culture-invariant

CultureInfo.InvariantCulture is correctly used for all ToString and Parse calls on numeric and date values. DateTimeOffset uses round-trip format o. Graph data is portable across locales.

---

### [INFO] XML documentation is complete

All public types and methods have XML docs. DriftSeverityClassifier documents the highest-first ordering behavior. GraphEwmaStateStore documents the ID format. EwmaDriftScorer documents the EWMA formula in its class-level summary. Meets template documentation requirements.

---

## Summary

| Severity | Count | Action Required |
|----------|-------|-----------------|
| CRITICAL | 0     | --              |
| HIGH     | 0     | --              |
| MEDIUM   | 3     | Should fix      |
| LOW      | 4     | Consider fixing |
| INFO     | 4     | No action       |

**MEDIUM items:**
1. Colon-in-scopeIdentifier ambiguity in deterministic IDs -- add validation or escape
2. Sequential N+1 in GetStatesAsync -- parallelize with Task.WhenAll
3. Negative deviation undocumented behavior in classifier -- guard or document

**LOW items:**
1. Missing null check on DriftSeverityClassifier.Classify(config) parameter
2-4. Three untested error propagation paths (GetState failure, SaveState failure, exception paths in GraphEwmaStateStore)

**Verdict: APPROVE** -- No CRITICAL or HIGH issues. The EWMA math is correct, thread safety is sound, architecture is clean, serialization is robust. The MEDIUM items are real but non-blocking: the colon ambiguity requires a consumer to intentionally pass colons in scope identifiers (unlikely), the N+1 is irrelevant until a networked graph backend exists, and the negative deviation case is prevented by the caller. The LOW test coverage gaps are worth adding but do not block merge.

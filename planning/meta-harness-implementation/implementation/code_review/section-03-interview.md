# Section 03 — Code Review Interview

## Review Findings

### H1 + H2: Guard clauses in TraceScope — USER APPROVED, APPLIED

**Finding:** `ResolveDirectory` silently accepts invalid combinations (`CandidateId` without `OptimizationRunId`, `TaskId` without `CandidateId`). `ForExecution` accepted `Guid.Empty`, producing a valid-looking but wrong path.

**Decision:** Add guard clauses despite the spec saying "no validation in domain records." The user agreed that silent wrong paths are a data-loss scenario, not a business-rule violation — and guards here protect invariants rather than validate input.

**Applied:**
- `ForExecution` throws `ArgumentException` for `Guid.Empty`
- `ResolveDirectory` throws `InvalidOperationException` for inconsistent combinations
- Added 3 tests: `ForExecution_WithEmptyGuid_Throws`, `ResolveDirectory_WithCandidateIdButNoOptimizationRunId_Throws`, `ResolveDirectory_WithTaskIdButNoCandidateId_Throws`

### L1: XML docs on ExampleResult — AUTO-FIXED

Added `<summary>` tags to `TaskId`, `Passed`, and `TokenCost` properties.

### M2/M3/N1/L2 — LET GO

- M2/M3: `new Dictionary` and `Array.Empty` defaults are acceptable — cast-to-mutate is adversarial
- N1: `Ts`/`Seq` property names match the spec's table — left as-is
- L2: Turn number bound is caller responsibility — no change needed

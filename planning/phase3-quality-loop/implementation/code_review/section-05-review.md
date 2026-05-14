# Section 05 Code Review: Drift Detection Application Interfaces

**Verdict: APPROVE -- 0 CRITICAL, 0 HIGH, 2 warnings (should fix), 3 suggestions (consider improving)**

Clean, well-structured application-layer contracts. All 7 interfaces, 4 DTOs, 4 validators, and 1 state record match their spec signatures exactly. XML documentation is thorough and template-quality. Immutability conventions followed throughout. Validator rules cover all spec-required constraints. Test coverage addresses happy paths and key failure cases. Two consistency items worth addressing before merge.

---

## Warnings (should fix)

### WARN-01: Return type inconsistency between IEwmaStateStore and sibling store interfaces

**Files:**
- `src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IEwmaStateStore.cs:13,17`
- `src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftBaselineStore.cs`
- `src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/IDriftAuditStore.cs`

`IEwmaStateStore.GetStateAsync` returns `Task<EwmaState?>` and `GetStatesAsync` returns `Task<IReadOnlyList<EwmaState>>` -- both without a `Result` wrapper. Meanwhile `IDriftBaselineStore.GetBaselineAsync` returns `Task<Result<DriftBaseline?>>` and `IDriftAuditStore.GetRecordsAsync` returns `Task<Result<IReadOnlyList<DriftAuditRecord>>>`.

The spec Key Design Decision #3 explicitly justifies this: "GetStateAsync returns nullable (null = not initialized)." This makes semantic sense -- EWMA reads are "data might not exist yet" rather than "the operation might fail." However, the infrastructure implementation still needs I/O (graph store or file reads) which can fail. If `GetStatesAsync` hits a deserialization error or I/O failure, there is no `Result` envelope to carry the error -- the only option is to throw.

This is spec-conformant, so not blocking. But it creates an inconsistency where implementors of `IEwmaStateStore` must throw on infrastructure failures while implementors of `IDriftBaselineStore` return `Result.Fail(...)`. Worth reconciling before the interface ships to template consumers.

**Recommendation:** Either wrap all IEwmaStateStore reads in `Result<T>` for consistency (returns `Result<EwmaState?>` and `Result<IReadOnlyList<EwmaState>>`), or add an XML doc `<exception>` tag on IEwmaStateStore methods documenting which exceptions callers should expect. Prefer the former -- it matches the Result pattern used everywhere else in this codebase.

---

### WARN-02: DriftAuditQuery validator does not validate Start-only or End-only scenarios

**File:** `src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/DriftAuditQueryValidator.cs:13-15`

The validator only fires when both Start and End are provided (`.When(x => x.Start.HasValue && x.End.HasValue)`). This is correct per spec. However, a query with `Start = null, End = 2024-01-01` (end-only) is semantically questionable -- "give me all records before this date with no lower bound" might return unbounded result sets in production. Similarly, `Start = 2024-01-01, End = null` is "everything after this date."

Not a correctness bug -- the spec allows it. But for a production template, adding documentation clarifying the behavior when only one bound is provided would help consumers understand the unbounded behavior.

**Recommendation:** Add XML doc remarks on `DriftAuditQuery.Start` and `DriftAuditQuery.End` clarifying the behavior when only one is provided. Consider whether a `MaxResults` or pagination property belongs on this DTO for production use.

---

## Suggestions (consider improving)

### SUGGEST-01: EwmaState.DeterministicId is vulnerable to ScopeIdentifier values containing colons

**File:** `src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/EwmaState.cs:31`

```csharp
public string DeterministicId => $"ewma:{Scope}:{ScopeIdentifier}:{Dimension}";
```
If `ScopeIdentifier` contains a colon (e.g., `"agent:v2"` or a URI-style identifier), the deterministic ID becomes ambiguous and un-parseable. Example: `"ewma:Skill:agent:v2:Faithfulness"` -- is the scope identifier `"agent:v2"` or `"agent"`?

This is the same pattern used by `DriftBaseline` in Domain (`"driftbaseline:{scope}:{identifier}"`), so this is a project-wide design choice rather than a section-05 bug. Still worth noting: if downstream consumers ever need to parse these IDs back into components, the colon delimiter will break.

**Options:**
1. Document that `ScopeIdentifier` must not contain colons (add a validator rule on DTOs that accept it).
2. Use a delimiter unlikely to appear in identifiers (e.g., `|` or `::` double-colon).
3. URL-encode the ScopeIdentifier segment.

Low priority since this is consistent with the existing domain convention. Flag for the broader project backlog.

---

### SUGGEST-02: Missing test coverage for DriftAuditQuery with one-sided time window

**File:** `src/Content/Tests/Application.AI.Common.Tests/Interfaces/DriftDetection/DriftDetectionDtoValidatorTests.cs`

The tests cover:
- Both Start and End null (passes) -- line 129
- Start after End (fails) -- line 115

Missing:
- Start provided, End null (should pass)
- Start null, End provided (should pass)
- Start equals End (should this pass or fail? `LessThan` means equal would fail)

The `Start == End` edge case is particularly noteworthy. `LessThan` (not `LessThanOrEqualTo`) means a query where `Start == End` will fail validation. This is probably correct (a zero-width window is useless), but a test documenting this behavior would be valuable.

**Recommended additional tests:**

```csharp
[Fact]
public void DriftAuditQueryValidator_OnlyStartProvided_Passes()
{
    var query = new DriftAuditQuery { Start = DateTimeOffset.UtcNow };
    var result = _auditValidator.TestValidate(query);
    result.ShouldNotHaveAnyValidationErrors();
}

[Fact]
public void DriftAuditQueryValidator_StartEqualsEnd_Fails()
{
    var now = DateTimeOffset.UtcNow;
    var query = new DriftAuditQuery { Start = now, End = now };
    var result = _auditValidator.TestValidate(query);
    result.ShouldHaveValidationErrorFor(x => x.Start);
}
```

Similarly, `DriftHistoryQueryValidator` uses `LessThan` (not `LessThanOrEqualTo`), and there is no test for `Start == End`. Same edge case applies.

---

### SUGGEST-03: DriftBaselineUpdateRequest test name is slightly misleading

**File:** `src/Content/Tests/Application.AI.Common.Tests/Interfaces/DriftDetection/DriftDetectionDtoTests.cs:37`

The test `DriftBaselineUpdateRequest_RequiresValidScope` only verifies DTO construction and property assignment -- it does not test validation. The name implies a validator check. Consider renaming to `DriftBaselineUpdateRequest_Construction_SetsAllProperties` for consistency with the other DTO test names (`DriftHistoryQuery_Construction_SetsAllProperties`, `EwmaState_Construction_WithScopeDimensionAndInitialValues`).

---

## Positive Observations

1. **Spec fidelity:** All interface signatures, DTO shapes, and validator rules match the spec exactly. No drift from the design.

2. **XML documentation quality:** Every public type and member has XML docs. The `IDriftBaselineStore` and `IDriftScorer` docs include keyed DI key names (`"graph"`, `"in_memory"`, `"ewma"`), which is excellent for template consumers who need to understand the DI wiring.

3. **Immutability:** All DTOs use `sealed record` with `init`-only properties. Collections use `IReadOnlyDictionary` and `IReadOnlyList`. No mutation paths.

4. **Notifier/Channel composite pattern:** Clean separation mirrors the escalation layer pattern. `IDriftNotifier` as composite dispatcher, `IDriftNotificationChannel` as individual sink. Cross-references in XML docs link the two.

5. **Validator coverage:** All 4 validators cover the spec rule table. The `DriftAuditQueryValidator` correctly uses `.When()` guard for the optional-fields scenario. `DriftHistoryQueryValidator` enforces both `ScopeIdentifier` not-empty and temporal ordering.

6. **Test organization:** Two focused test classes (DTO construction + validator rules) rather than one monolithic class. Test names follow `MethodName_Scenario_ExpectedResult` convention.

7. **CancellationToken consistency:** Every async interface method accepts `CancellationToken ct` as its final parameter. No omissions.

---

## Spec Compliance Summary

| Spec Requirement | Status |
|---|---|
| 7 interfaces (IDriftDetectionService, IDriftBaselineStore, IDriftScorer, IDriftAuditStore, IDriftNotificationChannel, IDriftNotifier, IEwmaStateStore) | All present, signatures match |
| 4 DTOs (DriftEvaluationRequest, DriftBaselineUpdateRequest, DriftHistoryQuery, DriftAuditQuery) | All present, shapes match |
| EwmaState record with DeterministicId | Present, formula matches |
| 4 validators with spec rule table | All present, rules match |
| DriftDetectionDtoTests (4+ tests) | 6 tests (exceeds spec) |
| DriftDetectionDtoValidatorTests (6+ tests) | 10 tests (exceeds spec) |
| Files in Application.AI.Common/Interfaces/DriftDetection/ | Correct placement |
| Tests in Application.AI.Common.Tests/Interfaces/DriftDetection/ | Correct placement |

---

## Verdict

**APPROVE.** No blocking issues. The two warnings are design consistency items that could be deferred to a follow-up cleanup pass without risk. Interface contracts are clean, spec-conformant, and ready for infrastructure implementations in Sections 7-10 and 14.

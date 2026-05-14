# Section 02 Code Review: Learnings Log Domain Models

**Verdict: APPROVE with 2 warnings (should fix) and 3 suggestions (consider)**

No CRITICAL or HIGH issues found. The models are well-structured, immutable, follow established conventions from DriftDetection/Escalation, and have thorough XML documentation. Test coverage is solid.

---

## Warnings (should fix)

### WARNING-01: `LearningCategory` and `LearningSourceType` enums missing explicit integer values

**Files:**
- `src/Content/Domain/Domain.AI/Learnings/LearningCategory.cs`
- `src/Content/Domain/Domain.AI/Learnings/LearningSourceType.cs`

`DecayClass` correctly assigns explicit values (`Volatile = 0, Stable = 1, Permanent = 2`), and Section 01 established this as a convention (SUGGESTION-01 applied explicit values to `DriftScope`). `LearningCategory` and `LearningSourceType` rely on implicit ordinals.

This matters because these enums will be serialized to a graph store. If a member is inserted in the middle later, all downstream values shift -- breaking persisted data.

```csharp
// Current (implicit)
public enum LearningCategory
{
    FactualCorrection,
    StylePreference,
    ToolUsagePattern,
    DomainKnowledge,
    InstructionUpdate
}

// Recommended (explicit)
public enum LearningCategory
{
    FactualCorrection = 0,
    StylePreference = 1,
    ToolUsagePattern = 2,
    DomainKnowledge = 3,
    InstructionUpdate = 4
}
```

Same fix applies to `LearningSourceType`.

### WARNING-02: `LearningCategory` XML doc uses `->` instead of proper cross-references

**File:** `src/Content/Domain/Domain.AI/Learnings/LearningCategory.cs:6-7`

The summary says `FactualCorrection -> Permanent, StylePreference -> Stable` using plain-text arrows. Every other cross-reference in this diff and in the DriftDetection models uses proper `<see cref="..."/>` tags. This is a template project where XML docs are teaching material -- consistency matters.

```xml
<!-- Current -->
/// <see cref="FactualCorrection"/> -> Permanent,
/// <see cref="StylePreference"/> -> Stable, etc.

<!-- Recommended -->
/// <see cref="FactualCorrection"/> maps to <see cref="DecayClass.Permanent"/>,
/// <see cref="StylePreference"/> maps to <see cref="DecayClass.Stable"/>, etc.
```

---

## Suggestions (consider)

### SUGGESTION-01: `LearningScope` naming divergence from `DriftScope`

`DriftScope` is an enum (`Agent`, `Skill`, `TaskType`). `LearningScope` is a record with `AgentId`/`TeamId`/`IsGlobal`. Both represent "scope" in their respective domains, but the structural difference could confuse template consumers.

This is actually a defensible design choice -- drift scope is a simple hierarchy level while learning scope carries identity data. The naming collision is mild. Documenting the distinction (e.g., a `<remarks>` note on `LearningScope` noting it is a value object, not a hierarchy enum like `DriftScope`) would help.

Not blocking. File if you agree: add a one-line `<remarks>` clarifying the distinction.

### SUGGESTION-02: Missing `LearningSourceType` and `DecayClass` member count tests

The test file has `LearningCategory_HasExactlyFiveMembers` (line 303-306) but no equivalent for `LearningSourceType` or `DecayClass`. `DriftDetectionDomainModelTests` follows the pattern of testing member counts for all enums (`DriftDimension_HasExactlySixMembers`). Adding these would catch accidental enum additions.

```csharp
[Fact]
public void LearningSourceType_HasExactlyFiveMembers()
{
    Assert.Equal(5, Enum.GetValues<LearningSourceType>().Length);
}

[Fact]
public void DecayClass_HasExactlyThreeMembers()
{
    Assert.Equal(3, Enum.GetValues<DecayClass>().Length);
}
```

### SUGGESTION-03: Test file missing JSON round-trip test

`DriftDetectionDomainModelTests` includes `DriftAuditRecord_JsonRoundTrip_PreservesEquality` (line 271-291). No equivalent exists for `LearningEntry` or `WeightedLearning`. Since these records will be serialized to/from graph storage, a round-trip test would catch serialization issues early (e.g., `DateTimeOffset` precision loss, `required` property deserialization failures).

Not blocking -- validation of serialization could reasonably live in the Infrastructure layer tests. But the DriftDetection precedent suggests it belongs here.

---

## What Looks Good

1. **Immutability:** All records are `sealed record` with `init`-only properties. `required` keyword used correctly on all mandatory fields. Optional fields (`LastAccessedAt`, `LastReinforcedAt`, `DeleteReason`) omit `required` and default to null/false. No mutable collections anywhere.

2. **XML Documentation:** Every public type and member has XML docs. The `<remarks>` blocks on `LearningEntry` and `LearningScope` are particularly useful -- they document scoring formulas, decay behavior, and scope resolution semantics that template consumers will need.

3. **Cross-domain consistency:** `LearningProvenance` mirrors the provenance concept from `DriftAuditRecord` (pipeline, task, timestamp). `LearningSource` with `SourceType` + `SourceId` follows the same discriminated-payload pattern as `DriftResolution` with `ResolvedBy` + `ResolutionId`.

4. **Naming:** PascalCase throughout, no abbreviations, property names are self-documenting. `FeedbackWeight`/`FreshnessScore`/`RelevanceScore` are clear and domain-appropriate.

5. **Test quality:** 260 lines, 16 test methods covering all types. The `CreateMinimalEntry()` helper avoids duplication. Theory tests for enum members follow the exact pattern from `DriftDetectionDomainModelTests`. Default-value tests (`DefaultFeedbackWeight_IsOne`, `DefaultOptionalFields_AreNull`) verify the contract that downstream handlers depend on.

6. **`DecayClass` explicit values:** Already follows the convention established in Section 01's review.

7. **File sizes:** All files are well under 150 lines. Single type per file.

---

## Summary

| Priority | Count | Items |
|----------|-------|-------|
| CRITICAL | 0 | -- |
| HIGH | 0 | -- |
| WARNING | 2 | Explicit enum values on `LearningCategory`/`LearningSourceType`; XML doc arrow syntax |
| SUGGESTION | 3 | `LearningScope` remarks note; member count tests; JSON round-trip test |

**Recommendation:** Fix WARNING-01 (serialization safety) and WARNING-02 (template doc consistency). The suggestions are low-priority improvements that can be addressed now or deferred.

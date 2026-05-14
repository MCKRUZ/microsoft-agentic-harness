# Section 04 Code Review: LearningsConfig, Validator, and Tests

**Verdict: APPROVE WITH CONDITIONS -- 1 HIGH issue (must fix), 2 warnings (should fix), 2 suggestions**

The config POCO, validator, and test suite are well-structured and follow the established DriftDetectionConfig/EscalationConfig pattern closely. XML documentation is thorough. Validation rules correctly match property constraints. Test coverage is strong with good boundary-value testing. One incomplete change in AIConfig.cs must be addressed before merge.

---

## HIGH Issues (must fix)

### HIGH-01: Missing `Learnings` property on AIConfig.cs

**File:** `src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs`

The diff adds the `using Domain.Common.Config.AI.Learnings;` directive (line 6) and updates the hierarchy comment (line 36), but does **not** add the actual `Learnings` property to the class. The spec (section-04-learnings-config.md, line 194) explicitly requires:

```csharp
/// <summary>
/// Cross-session learnings configuration controlling feedback blending,
/// temporal decay, pruning schedules, and drift baseline adjustment.
/// </summary>
public LearningsConfig Learnings { get; set; } = new();
```

Without this property, `IConfiguration.GetSection("AppConfig:AI:Learnings")` will never bind through the `AIConfig` object graph. The using directive becomes dead code (compiler warning CS8019). Downstream sections (18: DI registration, 19: appsettings) that depend on `AIConfig.Learnings` will fail at compile time.

**Fix:** Add the property to `AIConfig.cs` after the `DriftDetection` property, with the XML doc from the spec.

---

## Warnings (should fix)

### WARNING-01: No cross-property validation for VolatileShelfLifeDays < StableShelfLifeDays

**File:** `src/Content/Application/Application.Core/Validation/LearningsConfigValidator.cs`

The domain semantics imply `Volatile` learnings should have a shorter shelf life than `Stable` learnings -- the names themselves encode this invariant. The validator checks each is `> 0` independently but does not enforce `VolatileShelfLifeDays < StableShelfLifeDays`. A misconfiguration like `Volatile=365, Stable=7` would pass validation while inverting the decay semantics.

The DriftDetectionConfigValidator has a similar cross-property rule (`Warn < Alert < Escalate`), so there is precedent in this codebase.

**Fix:** Add a cross-property rule:

```csharp
RuleFor(x => x.VolatileShelfLifeDays)
    .LessThan(x => x.StableShelfLifeDays)
    .WithMessage("VolatileShelfLifeDays must be less than StableShelfLifeDays.");
```

And add a corresponding test (`Validate_VolatileShelfLifeExceedsStable_HasError`).

---

### WARNING-02: No validation for FeedbackAlpha <= FeedbackCeiling semantic constraint

**File:** `src/Content/Application/Application.Core/Validation/LearningsConfigValidator.cs`

`FeedbackAlpha` is the blending weight and `FeedbackCeiling` is the maximum influence. In a typical EMA-based scoring formula, it makes no sense for the blending weight to exceed the ceiling -- the ceiling would never be reached, or the alpha would be effectively capped. A config where `FeedbackAlpha=0.9, FeedbackCeiling=0.3` is technically valid per current rules but semantically suspect.

This is lower confidence than WARNING-01 because the exact scoring formula is not in this diff and the relationship depends on implementation. If the formula clamps independently, this may not matter. But it is worth considering.

**Fix (if applicable):** Add a cross-property rule:

```csharp
RuleFor(x => x.FeedbackAlpha)
    .LessThanOrEqualTo(x => x.FeedbackCeiling)
    .WithMessage("FeedbackAlpha should not exceed FeedbackCeiling.");
```

If the scoring formula makes this irrelevant, document why in a code comment on the validator.

---

## Suggestions (consider)

### SUGGESTION-01: Null StoreProvider not tested separately from empty

**File:** `src/Content/Tests/Application.Core.Tests/Validation/LearningsConfigValidatorTests.cs`

The test `Validate_EmptyStoreProvider_HasError` (line 247) covers `""`. FluentValidation's `NotEmpty()` also rejects `null`. Since `StoreProvider` has a default of `"graph"`, null can only occur via explicit assignment (e.g., `config.StoreProvider = null!`), but a null test would be more defensive and documents the intent.

---

### SUGGESTION-02: Consider whitespace-only StoreProvider test

**File:** `src/Content/Application/Application.Core/Validation/LearningsConfigValidator.cs:39`

`NotEmpty()` rejects null and empty string, but does **not** reject whitespace-only strings like `"  "`. If whitespace-only is also invalid (likely -- it would fail keyed DI lookup), consider using `NotEmpty()` combined with a `Must(x => !string.IsNullOrWhiteSpace(x))` or just `Matches(@"^\S+$")`. Low priority since this is an unlikely misconfiguration.

---

## Positive Observations

1. **Test pattern is solid.** `CreateValidConfig()` baseline with single-field mutation per test is the right approach. Every validated property has both "reject bad" and "accept boundary" tests.
2. **Boundary values are well-covered.** Zero, negative, above-max, and exact-boundary tests for all numeric ranges. `DiversityInjectionRatio` correctly allows `[0, 0.5]` inclusive on both ends.
3. **Config binding test is valuable.** `BindsFromAppSettingsJson` catches deserialization mismatches (wrong types, missing properties) that pure validator tests miss.
4. **XML docs are thorough.** The hierarchy diagram, `<c>` code references, `<value>` tags for defaults, and remarks on constraint ranges all serve the template consumer audience.
5. **Sealed validator** follows project convention.
6. **20 tests for 8 validated properties** -- good ratio, no gaps in the spec's test list.

---

## Files Reviewed

| File | Lines | Verdict |
|------|-------|---------|
| `src/Content/Domain/Domain.Common/Config/AI/Learnings/LearningsConfig.cs` | 94 | Clean |
| `src/Content/Application/Application.Core/Validation/LearningsConfigValidator.cs` | 42 | Clean |
| `src/Content/Tests/Application.Core.Tests/Validation/LearningsConfigValidatorTests.cs` | 308 | Clean |
| `src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs` (diff) | 8 lines changed | **Missing property** |

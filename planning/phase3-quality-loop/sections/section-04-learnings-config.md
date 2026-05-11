# Section 4: Learnings Log Configuration

## Overview

This section creates the `LearningsConfig` configuration class and its FluentValidation validator for the learnings subsystem. The config lives in the Domain.Common config hierarchy (matching `EscalationConfig`, `ResilienceConfig` patterns) and controls feedback blending, temporal decay, pruning schedules, and drift baseline adjustment thresholds.

## Dependencies

- **Section 2 (learnings domain):** Defines `DecayClass` enum. The config references shelf-life semantics but uses only primitive types -- no compile-time dependency on Domain.AI.
- **Blocked by this section:** Section 11 (decay service), Section 12 (learnings store), Section 18 (DI registration), Section 19 (appsettings).

## Config Properties

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| `Enabled` | bool | true | Master toggle. When disabled, all learnings operations return success no-ops. |
| `StoreProvider` | string | "graph" | Keyed DI provider for `ILearningsStore` ("graph" or "in_memory"). |
| `FeedbackAlpha` | double | 0.25 | EMA blending weight for feedback in recall scoring. |
| `FeedbackCeiling` | double | 0.3 | Maximum influence feedback can exert on final recall score. |
| `DiversityInjectionRatio` | double | 0.15 | Fraction of recall results replaced by random non-feedback-optimized learnings. |
| `VolatileShelfLifeDays` | int | 7 | Freshness decay window for `DecayClass.Volatile` learnings. |
| `StableShelfLifeDays` | int | 180 | Freshness decay window for `DecayClass.Stable` learnings. |
| `PruneIntervalHours` | int | 24 | How often the `LearningsPruningBackgroundService` runs. |
| `BaselineAdjustmentThreshold` | double | 0.8 | Minimum `FeedbackWeight` before learning can trigger drift baseline recalculation. |
| `BiasCorrection` | bool | true | When enabled and `UpdateCount < 5`, applies bias-corrected EMA. |

## Files to Create

| File | Project | Purpose |
|------|---------|---------|
| `src/Content/Domain/Domain.Common/Config/AI/Learnings/LearningsConfig.cs` | Domain.Common | Config POCO with defaults |
| `src/Content/Application/Application.Core/Validation/LearningsConfigValidator.cs` | Application.Core | FluentValidation rules |
| `src/Content/Tests/Application.Core.Tests/Validation/LearningsConfigValidatorTests.cs` | Application.Core.Tests | Validator + binding tests |

## Tests First

File: `src/Content/Tests/Application.Core.Tests/Validation/LearningsConfigValidatorTests.cs`

```csharp
namespace Application.Core.Tests.Validation;

public class LearningsConfigValidatorTests
{
    private readonly LearningsConfigValidator _validator = new();

    // Test: Validate_ValidConfig_NoErrors
    // Test: Validate_FeedbackAlphaZero_HasError
    // Test: Validate_FeedbackAlphaNegative_HasError
    // Test: Validate_FeedbackAlphaAboveOne_HasError
    // Test: Validate_FeedbackAlphaExactlyOne_Allowed
    // Test: Validate_FeedbackCeilingZero_HasError
    // Test: Validate_FeedbackCeilingNegative_HasError
    // Test: Validate_FeedbackCeilingAboveOne_HasError
    // Test: Validate_DiversityRatioNegative_HasError
    // Test: Validate_DiversityRatioAboveHalf_HasError
    // Test: Validate_DiversityRatioZero_Allowed (disables diversity, valid choice)
    // Test: Validate_DiversityRatioExactlyHalf_Allowed (boundary inclusive)
    // Test: Validate_VolatileShelfLifeZero_HasError
    // Test: Validate_VolatileShelfLifeNegative_HasError
    // Test: Validate_StableShelfLifeZero_HasError
    // Test: Validate_PruneIntervalZero_HasError
    // Test: Validate_BaselineAdjustmentThresholdZero_HasError
    // Test: Validate_BaselineAdjustmentThresholdAboveOne_HasError
    // Test: Validate_EmptyStoreProvider_HasError
    // Test: LearningsConfig_BindsFromJson_Correctly

    private static LearningsConfig CreateValidConfig() => new()
    {
        Enabled = true,
        StoreProvider = "graph",
        FeedbackAlpha = 0.25,
        FeedbackCeiling = 0.3,
        DiversityInjectionRatio = 0.15,
        VolatileShelfLifeDays = 7,
        StableShelfLifeDays = 180,
        PruneIntervalHours = 24,
        BaselineAdjustmentThreshold = 0.8,
        BiasCorrection = true
    };
}
```

## Implementation

### LearningsConfig

File: `src/Content/Domain/Domain.Common/Config/AI/Learnings/LearningsConfig.cs`

Namespace: `Domain.Common.Config.AI.Learnings`

```csharp
namespace Domain.Common.Config.AI.Learnings;

/// <summary>
/// Root configuration for the cross-session learnings subsystem.
/// Bound from <c>AppConfig:AI:Learnings</c> in appsettings.json.
/// </summary>
public class LearningsConfig
{
    /// <summary>Master toggle for the learnings subsystem.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Keyed DI provider for ILearningsStore ("graph" or "in_memory").</summary>
    public string StoreProvider { get; set; } = "graph";

    /// <summary>EMA blending weight for feedback in recall scoring formula.</summary>
    /// <value>Default: 0.25</value>
    public double FeedbackAlpha { get; set; } = 0.25;

    /// <summary>Maximum influence feedback can exert on final recall score.</summary>
    /// <value>Default: 0.3</value>
    public double FeedbackCeiling { get; set; } = 0.3;

    /// <summary>Fraction of recall results replaced by random non-feedback-optimized learnings.</summary>
    /// <value>Default: 0.15</value>
    public double DiversityInjectionRatio { get; set; } = 0.15;

    /// <summary>Shelf life in days for Volatile decay class learnings.</summary>
    /// <value>Default: 7</value>
    public int VolatileShelfLifeDays { get; set; } = 7;

    /// <summary>Shelf life in days for Stable decay class learnings.</summary>
    /// <value>Default: 180</value>
    public int StableShelfLifeDays { get; set; } = 180;

    /// <summary>Interval in hours for the background pruning service.</summary>
    /// <value>Default: 24</value>
    public int PruneIntervalHours { get; set; } = 24;

    /// <summary>Minimum FeedbackWeight before learning can trigger drift baseline adjustment.</summary>
    /// <value>Default: 0.8</value>
    public double BaselineAdjustmentThreshold { get; set; } = 0.8;

    /// <summary>Whether to apply bias-corrected EMA for new learnings (UpdateCount &lt; 5).</summary>
    /// <value>Default: true</value>
    public bool BiasCorrection { get; set; } = true;
}
```

### LearningsConfigValidator

File: `src/Content/Application/Application.Core/Validation/LearningsConfigValidator.cs`

```csharp
namespace Application.Core.Validation;

public sealed class LearningsConfigValidator : AbstractValidator<LearningsConfig>
{
    public LearningsConfigValidator()
    {
        RuleFor(x => x.FeedbackAlpha)
            .GreaterThan(0).WithMessage("FeedbackAlpha must be > 0.")
            .LessThanOrEqualTo(1).WithMessage("FeedbackAlpha must be <= 1.");

        RuleFor(x => x.FeedbackCeiling)
            .GreaterThan(0).WithMessage("FeedbackCeiling must be > 0.")
            .LessThanOrEqualTo(1).WithMessage("FeedbackCeiling must be <= 1.");

        RuleFor(x => x.DiversityInjectionRatio)
            .GreaterThanOrEqualTo(0).WithMessage("DiversityInjectionRatio must be >= 0.")
            .LessThanOrEqualTo(0.5).WithMessage("DiversityInjectionRatio must be <= 0.5.");

        RuleFor(x => x.VolatileShelfLifeDays)
            .GreaterThan(0).WithMessage("VolatileShelfLifeDays must be > 0.");

        RuleFor(x => x.StableShelfLifeDays)
            .GreaterThan(0).WithMessage("StableShelfLifeDays must be > 0.");

        RuleFor(x => x.PruneIntervalHours)
            .GreaterThan(0).WithMessage("PruneIntervalHours must be > 0.");

        RuleFor(x => x.BaselineAdjustmentThreshold)
            .GreaterThan(0).WithMessage("BaselineAdjustmentThreshold must be > 0.")
            .LessThanOrEqualTo(1).WithMessage("BaselineAdjustmentThreshold must be <= 1.");

        RuleFor(x => x.StoreProvider)
            .NotEmpty().WithMessage("StoreProvider must be configured (e.g., 'graph' or 'in_memory').");
    }
}
```

### AIConfig.cs Update

File to modify: `src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs`

```csharp
using Domain.Common.Config.AI.Learnings;

// Add property:
/// <summary>
/// Cross-session learnings configuration controlling feedback blending,
/// temporal decay, pruning schedules, and drift baseline adjustment.
/// </summary>
public LearningsConfig Learnings { get; set; } = new();
```

## Verification

```powershell
dotnet build src/AgenticHarness.slnx
dotnet test src/Content/Tests/Application.Core.Tests/Application.Core.Tests.csproj --filter "FullyQualifiedName~LearningsConfig"
```

## Implementation Notes

### Actual Files Created/Modified
| File | Action |
|------|--------|
| `src/Content/Domain/Domain.Common/Config/AI/Learnings/LearningsConfig.cs` | Created — Config POCO |
| `src/Content/Application/Application.Core/Validation/LearningsConfigValidator.cs` | Created — Validator |
| `src/Content/Tests/Application.Core.Tests/Validation/LearningsConfigValidatorTests.cs` | Created — 21 tests |
| `src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs` | Modified — Added Learnings property + using + hierarchy comment |

### Deviations from Plan
- Code review caught that the Learnings property was initially omitted from AIConfig.cs (only using + comment were added). Fixed before commit.
- Total: 21 tests (spec listed ~20 test comments, all implemented + defaults verification + JSON binding)

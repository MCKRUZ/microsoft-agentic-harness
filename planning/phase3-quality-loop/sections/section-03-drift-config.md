# Section 03: Drift Detection Configuration

## Overview

This section creates the `DriftDetectionConfig` configuration class and its FluentValidation validator. The config lives in `Domain.Common` (config POCO) and `Application.Core` (validator), bound from `AppConfig:AI:DriftDetection` in appsettings.json. It controls all tuning parameters for the EWMA-based drift detection system.

## Dependencies

- **Section 01 (drift-domain):** Provides `DriftDimension`, `DriftSeverity`, `DriftScope` enums referenced in config documentation. The config class itself does not import domain enums -- it uses primitive types only, matching the convention of `EscalationConfig` and `ResilienceConfig`.

## Files to Create

| File | Project | Purpose |
|------|---------|---------|
| `src/Content/Domain/Domain.Common/Config/AI/DriftDetection/DriftDetectionConfig.cs` | Domain.Common | Config POCO with defaults |
| `src/Content/Application/Application.Core/Validation/DriftDetectionConfigValidator.cs` | Application.Core | FluentValidation rules |
| `src/Content/Tests/Application.Core.Tests/Validation/DriftDetectionConfigValidatorTests.cs` | Application.Core.Tests | Validator + binding tests |

## Tests First

Test file: `src/Content/Tests/Application.Core.Tests/Validation/DriftDetectionConfigValidatorTests.cs`

Follow the exact pattern from `EscalationConfigValidatorTests`: a `CreateValidConfig()` factory returns a known-good baseline, and each test mutates one property to verify a specific validation rule.

```csharp
namespace Application.Core.Tests.Validation;

public class DriftDetectionConfigValidatorTests
{
    private readonly DriftDetectionConfigValidator _validator = new();

    // Test: DriftDetectionConfig default values match spec (EwmaLambda=0.2, etc.)
    // Verify: new DriftDetectionConfig() has EwmaLambda=0.2, ControlLimitWidth=3.0,
    //   MinSamplesForBaseline=20, BaselineWindowDays=7, WarnThresholdSigma=1.5,
    //   AlertThresholdSigma=2.5, EscalateThresholdSigma=3.0, Enabled=true,
    //   EscalationEnabled=true, AuditPath="data/audit"

    // Test: DriftConfigValidator rejects EwmaLambda <= 0
    // Test: DriftConfigValidator rejects EwmaLambda > 1
    // Test: DriftConfigValidator rejects WarnThreshold >= AlertThreshold
    // Test: DriftConfigValidator rejects AlertThreshold >= EscalateThreshold
    // Test: DriftConfigValidator rejects MinSamplesForBaseline <= 0
    // Test: DriftConfigValidator rejects negative ControlLimitWidth
    // Test: DriftConfigValidator accepts valid configuration
    // Test: DriftDetectionConfig binds from appsettings JSON correctly

    private static DriftDetectionConfig CreateValidConfig() => new()
    {
        Enabled = true,
        EwmaLambda = 0.2,
        ControlLimitWidth = 3.0,
        MinSamplesForBaseline = 20,
        BaselineWindowDays = 7,
        WarnThresholdSigma = 1.5,
        AlertThresholdSigma = 2.5,
        EscalateThresholdSigma = 3.0,
        EscalationEnabled = true,
        AuditPath = "data/audit"
    };
}
```

### JSON Binding Test

Uses `ConfigurationBuilder().AddInMemoryCollection()` with key-value pairs matching `AppConfig:AI:DriftDetection:*`. Calls `.GetSection().Get<DriftDetectionConfig>()` and asserts all properties round-trip.

## Implementation

### DriftDetectionConfig

File: `src/Content/Domain/Domain.Common/Config/AI/DriftDetection/DriftDetectionConfig.cs`

Namespace: `Domain.Common.Config.AI.DriftDetection`

```csharp
namespace Domain.Common.Config.AI.DriftDetection;

/// <summary>
/// Configuration for the EWMA-based drift detection subsystem.
/// Bound from <c>AppConfig:AI:DriftDetection</c> in appsettings.json.
/// </summary>
/// <remarks>
/// Configuration hierarchy:
/// <code>
/// AppConfig.AI.DriftDetection
/// +-- Enabled                -- Master toggle for drift detection
/// +-- EwmaLambda             -- EWMA smoothing factor (0, 1]
/// +-- ControlLimitWidth      -- Sigma multiplier for control limits
/// +-- MinSamplesForBaseline  -- Minimum evaluations before baseline is valid
/// +-- BaselineWindowDays     -- Rolling window for baseline recalculation
/// +-- WarnThresholdSigma     -- Deviation triggering Warn severity
/// +-- AlertThresholdSigma    -- Deviation triggering Alert severity
/// +-- EscalateThresholdSigma -- Deviation triggering Escalate severity
/// +-- EscalationEnabled      -- Whether Escalate severity triggers Phase 2 escalation
/// +-- AuditPath              -- Directory for JSONL drift audit files
/// </code>
/// Threshold ordering invariant: Warn &lt; Alert &lt; Escalate.
/// Enforced by <c>DriftDetectionConfigValidator</c>.
/// </remarks>
public class DriftDetectionConfig
{
    /// <summary>
    /// Master toggle. When disabled, <c>DefaultDriftDetectionService</c>
    /// returns <c>Result.Success</c> with default/empty values for all operations.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// EWMA smoothing factor. Higher values weight recent observations more heavily.
    /// Must be in range (0, 1].
    /// </summary>
    /// <value>Default: 0.2</value>
    public double EwmaLambda { get; set; } = 0.2;

    /// <summary>
    /// Sigma multiplier for EWMA control limits (UCL/LCL).
    /// UCL = baseline_mean + L * sigma * sqrt(lambda / (2 - lambda)).
    /// </summary>
    /// <value>Default: 3.0</value>
    public double ControlLimitWidth { get; set; } = 3.0;

    /// <summary>
    /// Minimum number of evaluations required before a baseline is considered valid.
    /// </summary>
    /// <value>Default: 20</value>
    public int MinSamplesForBaseline { get; set; } = 20;

    /// <summary>
    /// Rolling window in days for baseline recalculation.
    /// </summary>
    /// <value>Default: 7</value>
    public int BaselineWindowDays { get; set; } = 7;

    /// <summary>
    /// Sigma deviation threshold for <c>DriftSeverity.Warn</c>.
    /// Must be less than <see cref="AlertThresholdSigma"/>.
    /// </summary>
    /// <value>Default: 1.5</value>
    public double WarnThresholdSigma { get; set; } = 1.5;

    /// <summary>
    /// Sigma deviation threshold for <c>DriftSeverity.Alert</c>.
    /// Must be between <see cref="WarnThresholdSigma"/> and <see cref="EscalateThresholdSigma"/>.
    /// </summary>
    /// <value>Default: 2.5</value>
    public double AlertThresholdSigma { get; set; } = 2.5;

    /// <summary>
    /// Sigma deviation threshold for <c>DriftSeverity.Escalate</c>.
    /// Must be greater than <see cref="AlertThresholdSigma"/>.
    /// </summary>
    /// <value>Default: 3.0</value>
    public double EscalateThresholdSigma { get; set; } = 3.0;

    /// <summary>
    /// Whether drift events at <c>DriftSeverity.Escalate</c> trigger the
    /// Phase 2 human escalation system via <c>IEscalationService</c>.
    /// </summary>
    /// <value>Default: true</value>
    public bool EscalationEnabled { get; set; } = true;

    /// <summary>
    /// Directory path for the JSONL drift audit store.
    /// </summary>
    /// <value>Default: "data/audit"</value>
    public string AuditPath { get; set; } = "data/audit";
}
```

### DriftDetectionConfigValidator

File: `src/Content/Application/Application.Core/Validation/DriftDetectionConfigValidator.cs`

```csharp
namespace Application.Core.Validation;

public sealed class DriftDetectionConfigValidator : AbstractValidator<DriftDetectionConfig>
{
    public DriftDetectionConfigValidator()
    {
        RuleFor(x => x.EwmaLambda)
            .GreaterThan(0).WithMessage("EwmaLambda must be > 0.")
            .LessThanOrEqualTo(1).WithMessage("EwmaLambda must be <= 1.");

        RuleFor(x => x.ControlLimitWidth)
            .GreaterThan(0).WithMessage("ControlLimitWidth must be > 0.");

        RuleFor(x => x.MinSamplesForBaseline)
            .GreaterThan(0).WithMessage("MinSamplesForBaseline must be > 0.");

        RuleFor(x => x.BaselineWindowDays)
            .GreaterThan(0).WithMessage("BaselineWindowDays must be > 0.");

        RuleFor(x => x.WarnThresholdSigma)
            .LessThan(x => x.AlertThresholdSigma)
            .WithMessage("WarnThresholdSigma must be less than AlertThresholdSigma.");

        RuleFor(x => x.AlertThresholdSigma)
            .LessThan(x => x.EscalateThresholdSigma)
            .WithMessage("AlertThresholdSigma must be less than EscalateThresholdSigma.");

        RuleFor(x => x.WarnThresholdSigma).GreaterThan(0);
        RuleFor(x => x.AlertThresholdSigma).GreaterThan(0);
        RuleFor(x => x.EscalateThresholdSigma).GreaterThan(0);

        RuleFor(x => x.AuditPath)
            .NotEmpty().WithMessage("AuditPath must be configured.");
    }
}
```

### AIConfig.cs Update

File to modify: `src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs`

Add using directive and property:
```csharp
using Domain.Common.Config.AI.DriftDetection;

// Add to AIConfig class:
/// <summary>
/// EWMA-based drift detection configuration for identifying quality regressions.
/// </summary>
public DriftDetectionConfig DriftDetection { get; set; } = new();
```

## Validation Rules Summary

| Property | Rule | Message |
|----------|------|---------|
| `EwmaLambda` | `> 0` and `<= 1` | "EwmaLambda must be > 0." / "<= 1." |
| `ControlLimitWidth` | `> 0` | "ControlLimitWidth must be > 0." |
| `MinSamplesForBaseline` | `> 0` | "MinSamplesForBaseline must be > 0." |
| `BaselineWindowDays` | `> 0` | "BaselineWindowDays must be > 0." |
| `WarnThresholdSigma` | `> 0` and `< AlertThresholdSigma` | ordering message |
| `AlertThresholdSigma` | `> 0` and `< EscalateThresholdSigma` | ordering message |
| `EscalateThresholdSigma` | `> 0` | implicit from alert < escalate |
| `AuditPath` | `NotEmpty` | "AuditPath must be configured." |

## Config Binding Path

The config binds at `AppConfig:AI:DriftDetection`. The binding chain:

1. `appsettings.json` contains `"AppConfig": { "AI": { "DriftDetection": { ... } } }`
2. `services.Configure<AppConfig>(configuration.GetSection("AppConfig"))` binds the whole tree
3. `IOptionsMonitor<AppConfig>.CurrentValue.AI.DriftDetection` provides runtime access

No new DI registration needed -- `DriftDetectionConfig` is reachable through the existing `AppConfig` -> `AIConfig` -> `DriftDetectionConfig` property chain.

## Downstream Consumers

- **Section 07 (EWMA scorer):** Reads `EwmaLambda`, `ControlLimitWidth`
- **Section 08 (drift service):** Reads `Enabled`, threshold sigmas, `EscalationEnabled`, `BaselineWindowDays`, `MinSamplesForBaseline`
- **Section 10 (drift audit):** Reads `AuditPath`
- **Section 19 (appsettings):** Adds the JSON block

## Implementation Notes

### Actual Files Created/Modified
| File | Action |
|------|--------|
| `src/Content/Domain/Domain.Common/Config/AI/DriftDetection/DriftDetectionConfig.cs` | Created — Config POCO |
| `src/Content/Application/Application.Core/Validation/DriftDetectionConfigValidator.cs` | Created — Validator |
| `src/Content/Tests/Application.Core.Tests/Validation/DriftDetectionConfigValidatorTests.cs` | Created — 16 tests |
| `src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs` | Modified — Added DriftDetection property + using + hierarchy comment |

### Deviations from Plan
- Added explicit `.WithMessage()` on `.GreaterThan(0)` for all three threshold sigma rules (review fix — plan only showed `.WithMessage()` on `.LessThan()`)
- Added 3 boundary tests for zero thresholds (WarnThresholdSigmaZero, AlertThresholdSigmaZero, EscalateThresholdSigmaZero) not in original spec
- Total: 16 tests (spec had ~10 test comments, implementation covers all rules + boundary cases + binding)

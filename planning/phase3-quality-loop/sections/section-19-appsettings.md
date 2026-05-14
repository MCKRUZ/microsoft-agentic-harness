# Section 19: appsettings.json Configuration

## Overview

This section adds `DriftDetection` and `Learnings` JSON config blocks to both `appsettings.json` files (`Presentation.AgentHub` and `Presentation.ConsoleUI`). These blocks provide concrete values for the config POCOs defined in section-03 (`DriftDetectionConfig`) and section-04 (`LearningsConfig`), which bind automatically through the .NET Options pattern.

No C# code changes are needed beyond the JSON edits and two new `services.Configure<T>()` lines in the shared config registration method.

## Dependencies

- **Section 03 (drift-config):** Defines `DriftDetectionConfig` with properties: `Enabled`, `EwmaLambda`, `ControlLimitWidth`, `MinSamplesForBaseline`, `BaselineWindowDays`, `WarnThresholdSigma`, `AlertThresholdSigma`, `EscalateThresholdSigma`, `EscalationEnabled`, `AuditPath`.
- **Section 04 (learnings-config):** Defines `LearningsConfig` with properties: `Enabled`, `StoreProvider`, `FeedbackAlpha`, `FeedbackCeiling`, `DiversityInjectionRatio`, `VolatileShelfLifeDays`, `StableShelfLifeDays`, `PruneIntervalHours`, `BaselineAdjustmentThreshold`, `BiasCorrection`.
- **Section 18 (di-registration):** Updates `AIConfig.cs` to add `DriftDetection` and `Learnings` properties. Wires all services via DI.

## Files to Modify

| File | Change |
|------|--------|
| `src/Content/Presentation/Presentation.AgentHub/appsettings.json` | Add `DriftDetection` and `Learnings` blocks under `AppConfig.AI` |
| `src/Content/Presentation/Presentation.ConsoleUI/appsettings.json` | Add `DriftDetection` and `Learnings` blocks under `AppConfig.AI` |
| `src/Content/Presentation/Presentation.Common/Extensions/IServiceCollectionExtensions.cs` | Add two `services.Configure<>()` lines for direct injection |
| `src/Content/Tests/Presentation.Common.Tests/Extensions/IServiceCollectionExtensionsTests.cs` | Add config binding tests |

---

## Tests First

Add these tests to the existing `IServiceCollectionExtensionsTests` class at `src/Content/Tests/Presentation.Common.Tests/Extensions/IServiceCollectionExtensionsTests.cs`, following the exact pattern used by the Phase 2 `EscalationConfig_BindsFullStructure_FromAppsettings` and `ResilienceConfig_BindsFullStructure_FromAppsettings` tests.

```csharp
// Test: DriftDetectionConfig_BindsFromAppsettings
//   Arrange: Build IConfiguration from in-memory JSON with the full DriftDetection block.
//            Bind via configuration.GetSection("AppConfig:AI:DriftDetection").Get<DriftDetectionConfig>().
//   Assert: Enabled == true, EwmaLambda == 0.2, ControlLimitWidth == 3.0,
//           MinSamplesForBaseline == 20, BaselineWindowDays == 7,
//           WarnThresholdSigma == 1.5, AlertThresholdSigma == 2.5, EscalateThresholdSigma == 3.0,
//           EscalationEnabled == true, AuditPath == "data/audit".

// Test: LearningsConfig_BindsFromAppsettings
//   Arrange: Build IConfiguration from in-memory JSON with the full Learnings block.
//            Bind via configuration.GetSection("AppConfig:AI:Learnings").Get<LearningsConfig>().
//   Assert: Enabled == true, StoreProvider == "graph", FeedbackAlpha == 0.25,
//           FeedbackCeiling == 0.3, DiversityInjectionRatio == 0.15,
//           VolatileShelfLifeDays == 7, StableShelfLifeDays == 180,
//           PruneIntervalHours == 24, BaselineAdjustmentThreshold == 0.8,
//           BiasCorrection == true.

// Test: RegisterConfigSections_BindsDriftDetectionConfig
//   Arrange: Build IConfiguration with DriftDetection values. Call RegisterConfigSections().
//   Act: Resolve IOptionsMonitor<DriftDetectionConfig> from the service provider.
//   Assert: CurrentValue.Enabled == true, CurrentValue.EwmaLambda == 0.2.

// Test: RegisterConfigSections_BindsLearningsConfig
//   Arrange: Build IConfiguration with Learnings values. Call RegisterConfigSections().
//   Act: Resolve IOptionsMonitor<LearningsConfig> from the service provider.
//   Assert: CurrentValue.Enabled == true, CurrentValue.FeedbackAlpha == 0.25.
```

These tests use the same `ConfigurationBuilder().AddInMemoryCollection()` pattern as the existing Phase 2 config binding tests. The in-memory key format follows the flat-key convention: `["AppConfig:AI:DriftDetection:EwmaLambda"] = "0.2"`.

---

## Implementation Details

### Config Binding Architecture

The existing pipeline works as follows:

1. `Program.cs` calls `builder.Services.GetServices()` (in `Presentation.Common/Extensions/IServiceCollectionExtensions.cs`)
2. `GetServices()` calls `RegisterConfigSections(config)` which binds subsections to their POCOs
3. `services.Configure<AIConfig>(configuration.GetSection("AppConfig:AI"))` already handles recursive binding into `AIConfig`
4. Section 18 adds `DriftDetection` (type `DriftDetectionConfig`) and `Learnings` (type `LearningsConfig`) as properties on `AIConfig`
5. The nested binding automatically picks up `AppConfig:AI:DriftDetection:*` and `AppConfig:AI:Learnings:*`

For direct injection via `IOptionsMonitor<DriftDetectionConfig>` and `IOptionsMonitor<LearningsConfig>` (used by services like `EwmaDriftScorer` and `DefaultLearningDecayService`), two explicit `Configure<>()` calls must be added to `RegisterConfigSections`.

### RegisterConfigSections Update

In `src/Content/Presentation/Presentation.Common/Extensions/IServiceCollectionExtensions.cs`, add two lines after the existing `ResilienceConfig` registration:

```csharp
services.Configure<DriftDetectionConfig>(configuration.GetSection("AppConfig:AI:DriftDetection"));
services.Configure<LearningsConfig>(configuration.GetSection("AppConfig:AI:Learnings"));
```

Add the corresponding `using` directives for the config namespaces. The exact namespace depends on how sections 3 and 4 organize the files, but following the plan they will be in `Domain.Common.Config.AI` (alongside `GovernanceConfig` and `ResilienceConfig`).

### DriftDetection Block

Add inside `AppConfig.AI` (as a sibling of `Governance`, `Resilience`, `Permissions`, etc.):

```json
"DriftDetection": {
  "Enabled": true,
  "EwmaLambda": 0.2,
  "ControlLimitWidth": 3.0,
  "MinSamplesForBaseline": 20,
  "BaselineWindowDays": 7,
  "WarnThresholdSigma": 1.5,
  "AlertThresholdSigma": 2.5,
  "EscalateThresholdSigma": 3.0,
  "EscalationEnabled": true,
  "AuditPath": "data/audit"
}
```

Design notes:
- `Enabled: true` -- drift detection has no external dependencies; safe to enable by default.
- `EwmaLambda: 0.2` -- standard EWMA smoothing factor. Lower values (closer to 0) give more weight to historical data. 0.2 means 80% history / 20% current observation.
- `ControlLimitWidth: 3.0` -- 3-sigma control limits, the standard for statistical process control (99.7% of normal variation falls within 3 sigma).
- `MinSamplesForBaseline: 20` -- minimum evaluations before a baseline is statistically valid. Below this, drift detection is skipped for the scope.
- `BaselineWindowDays: 7` -- rolling window for baseline recalculation. Keeps baselines current.
- Thresholds are ordered: `Warn (1.5) < Alert (2.5) < Escalate (3.0)`. The `DriftConfigValidator` (section 03) enforces this ordering.
- `EscalationEnabled: true` -- when severity reaches Escalate, triggers Phase 2 `IEscalationService`. Can be disabled independently from drift detection itself.
- `AuditPath: "data/audit"` -- directory for JSONL audit files (used by `JsonlDriftAuditStore` in section 10).

### Learnings Block

Add inside `AppConfig.AI` (as a sibling alongside `DriftDetection`):

```json
"Learnings": {
  "Enabled": true,
  "StoreProvider": "graph",
  "FeedbackAlpha": 0.25,
  "FeedbackCeiling": 0.3,
  "DiversityInjectionRatio": 0.15,
  "VolatileShelfLifeDays": 7,
  "StableShelfLifeDays": 180,
  "PruneIntervalHours": 24,
  "BaselineAdjustmentThreshold": 0.8,
  "BiasCorrection": true
}
```

Design notes:
- `Enabled: true` -- learnings system has no external dependencies; safe to enable by default.
- `StoreProvider: "graph"` -- resolves to `GraphLearningsStore` via keyed DI. Alternative: `"in_memory"` for development/testing.
- `FeedbackAlpha: 0.25` -- blending weight for feedback in recall scoring. Formula: `finalScore = (1 - alpha) * relevance + alpha * min(feedback * freshness, ceiling)`. At 0.25, semantic relevance contributes 75% and feedback 25%.
- `FeedbackCeiling: 0.3` -- caps the maximum influence of feedback to prevent runaway amplification of popular learnings at the expense of relevance.
- `DiversityInjectionRatio: 0.15` -- replaces the bottom 15% of recall results with random non-feedback-optimized learnings to prevent filter bubbles. The `LearningsConfigValidator` (section 04) enforces this is in [0, 0.5].
- `VolatileShelfLifeDays: 7` -- volatile learnings (e.g., tool usage patterns) expire after 7 days without reinforcement.
- `StableShelfLifeDays: 180` -- stable learnings (e.g., style preferences) persist for 6 months without reinforcement.
- Permanent learnings (e.g., factual corrections) never decay regardless of these settings.
- `PruneIntervalHours: 24` -- the `LearningsPruningBackgroundService` (section 11) runs once per day.
- `BaselineAdjustmentThreshold: 0.8` -- when a learning's `FeedbackWeight` exceeds this value, it signals that the drift baseline should be adjusted (section 17 integration). High threshold ensures only well-validated learnings trigger baseline changes.
- `BiasCorrection: true` -- applies bias-corrected EMA for learnings with fewer than 5 updates, preventing early feedback scores from being overweighted.

### Placement in JSON

**AgentHub** (`src/Content/Presentation/Presentation.AgentHub/appsettings.json`):

The two new blocks go inside `AppConfig.AI`, as siblings of existing sections. Recommended placement after `Resilience` and before `Permissions` to keep quality/governance-related config grouped together:

```json
"AI": {
  "AgentFramework": { ... },
  "Governance": { ... },
  "Resilience": { ... },
  "DriftDetection": { ... },
  "Learnings": { ... },
  "Permissions": { ... },
  "Orchestration": { ... },
  "Skills": { ... },
  "Agents": { ... },
  "Rag": { ... }
}
```

**ConsoleUI** (`src/Content/Presentation/Presentation.ConsoleUI/appsettings.json`):

Same `DriftDetection` and `Learnings` blocks, same placement under `AppConfig.AI`. Both hosts get identical config since they share the same config class hierarchy via `IServiceCollectionExtensions.RegisterConfigSections`.

---

## Key Design Decisions

1. **Both subsystems enabled by default.** Unlike `Resilience` (which requires external provider endpoints), drift detection and learnings work entirely with in-memory/local state. No external dependencies means safe to enable out of the box.

2. **Both hosts get identical config.** Template consumers can diverge per-host later. The template ships with identical defaults.

3. **Explicit `Configure<>()` calls needed.** While `AIConfig` recursive binding works for accessing config via `IOptionsMonitor<AppConfig>`, services that inject `IOptionsMonitor<DriftDetectionConfig>` or `IOptionsMonitor<LearningsConfig>` directly need the explicit section binding. This matches the Phase 2 pattern where `EscalationConfig` and `ResilienceConfig` both have explicit `Configure<>()` lines.

4. **AuditPath relative to application base.** `"data/audit"` resolves relative to the working directory. The `JsonlDriftAuditStore` (section 10) creates the directory if it does not exist.

5. **StoreProvider uses keyed DI string.** `"graph"` maps to `GraphLearningsStore`, `"in_memory"` maps to `InMemoryLearningsStore`. The DI registration (section 18) resolves the default `ILearningsStore` from this config value.

---

## Verification

After applying all changes:

```powershell
dotnet build src/AgenticHarness.slnx
dotnet test src/Content/Tests/Presentation.Common.Tests/Presentation.Common.Tests.csproj --filter "FullyQualifiedName~IServiceCollectionExtensionsTests"
```

Expected: all existing config binding tests continue passing, plus the 4 new tests (`DriftDetectionConfig_BindsFromAppsettings`, `LearningsConfig_BindsFromAppsettings`, `RegisterConfigSections_BindsDriftDetectionConfig`, `RegisterConfigSections_BindsLearningsConfig`) pass.

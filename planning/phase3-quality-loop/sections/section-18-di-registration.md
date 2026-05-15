# Section 18: DI Registration

## Overview

This section wires all Phase 3 drift detection and learnings services into the DI containers across three projects. It updates the `AIConfig.cs` class with two new config properties (`DriftDetection`, `Learnings`), registers all implementations in `Infrastructure.AI/DependencyInjection.cs`, registers graph-backed learnings store in `Infrastructure.AI.KnowledgeGraph/DependencyInjection.cs`, and registers AG-UI notification channels in `Presentation.AgentHub/DependencyInjection.cs`. Additionally, the composition root in `Presentation.Common` must be updated to call `AddKnowledgeGraphDependencies` if it is not already wired.

**Layers:** Domain.Common (config model update), Infrastructure.AI (DI), Infrastructure.AI.KnowledgeGraph (DI), Presentation.AgentHub (DI)

## Dependencies

- **Section 03 (Drift Config):** `DriftDetectionConfig` class at `Domain.Common/Config/AI/DriftDetectionConfig.cs`
- **Section 04 (Learnings Config):** `LearningsConfig` class at `Domain.Common/Config/AI/LearningsConfig.cs`
- **Section 05 (Drift Interfaces):** `IDriftDetectionService`, `IDriftBaselineStore`, `IDriftScorer`, `IDriftAuditStore`, `IDriftNotificationChannel`, `IDriftNotifier`, `IEwmaStateStore`
- **Section 06 (Learnings Interfaces):** `ILearningsStore`, `ILearningDecayService`, `ILearningNotificationChannel`
- **Section 07 (EWMA Scorer):** `EwmaDriftScorer` implementing `IDriftScorer`
- **Section 08 (Drift Service):** `DefaultDriftDetectionService`, `CompositeDriftNotifier`, `DriftMetrics`
- **Section 09 (Baseline Store):** `GraphDriftBaselineStore`, `InMemoryDriftBaselineStore`
- **Section 10 (Drift Audit):** `JsonlDriftAuditStore`
- **Section 11 (Decay Service):** `DefaultLearningDecayService`, `LearningsPruningBackgroundService`
- **Section 12 (Learnings Store):** `GraphLearningsStore`, `InMemoryLearningsStore`
- **Section 14 (Drift SSE):** `AgUiDriftNotifier` implementing `IDriftNotificationChannel`
- **Section 15 (Learnings SSE):** `AgUiLearningNotifier` implementing `ILearningNotificationChannel`
- **Section 16 (Escalation Bridge):** `DriftEscalationBridge` implementing `IEscalationNotificationChannel`
- **Existing:** `IEscalationNotificationChannel`, `CompositeEscalationNotifier`, escalation DI pattern, `IKnowledgeGraphStore` keyed DI, `IOptionsMonitor<AppConfig>`, `TimeProvider`

## Tests First

All DI registration tests go in `src/Content/Tests/Infrastructure.AI.Tests/DependencyInjectionTests.cs` (extend the existing test class). Config binding tests go in the same file or a new `DriftLearningsDiTests.cs` if the existing file would exceed 400 lines.

```csharp
// File: src/Content/Tests/Infrastructure.AI.Tests/DriftLearningsDiTests.cs
// Namespace: Infrastructure.AI.Tests

// Uses the same CreateBaseServices() + OptionsMonitorStub pattern from DependencyInjectionTests.

// --- Drift Detection DI Tests ---

// Test: DriftDetection_Services_Resolve
//   Arrange: CreateBaseServices, register TimeProvider.System, AddInfrastructureAIDependencies
//   Act: resolve IDriftDetectionService
//   Assert: should not be null, should be DefaultDriftDetectionService

// Test: DriftDetection_KeyedScorer_Resolves_Ewma
//   Arrange: same base setup
//   Act: resolve IDriftScorer with key "ewma" via provider.GetRequiredKeyedService<IDriftScorer>("ewma")
//   Assert: should not be null, should be EwmaDriftScorer

// Test: DriftDetection_BaselineStore_Default_ResolvesFromConfig
//   Arrange: CreateBaseServices with AppConfig where a StoreProvider config value = "in_memory"
//            (or leave default "graph"). AddKnowledgeGraphDependencies + AddInfrastructureAIDependencies.
//   Act: resolve IDriftBaselineStore (non-keyed)
//   Assert: type matches the configured provider (GraphDriftBaselineStore or InMemoryDriftBaselineStore)
//   Note: the default (non-keyed) registration should read the config to select keyed impl.

// Test: DriftDetection_AuditStore_Resolves
//   Arrange: base setup
//   Act: resolve IDriftAuditStore
//   Assert: should be JsonlDriftAuditStore

// Test: DriftDetection_Notifier_Resolves
//   Arrange: base setup
//   Act: resolve IDriftNotifier
//   Assert: should be CompositeDriftNotifier

// Test: DriftDetection_EwmaStateStore_Resolves
//   Arrange: base setup (needs IKnowledgeGraphStore registered)
//   Act: resolve IEwmaStateStore
//   Assert: should not be null

// --- Learnings DI Tests ---

// Test: Learnings_Services_Resolve
//   Arrange: base setup
//   Act: resolve ILearningDecayService
//   Assert: should be DefaultLearningDecayService

// Test: Learnings_Store_Default_ResolvesFromConfig
//   Arrange: CreateBaseServices with AppConfig where Learnings.StoreProvider = "in_memory"
//   Act: resolve ILearningsStore (non-keyed)
//   Assert: should be InMemoryLearningsStore
//   Also test: with StoreProvider = "graph", should resolve GraphLearningsStore

// Test: LearningsPruningService_RegisteredWhenEnabled
//   Arrange: AppConfig { AI = { Learnings = { Enabled = true } } }
//   Act: resolve IHostedService collection
//   Assert: should contain LearningsPruningBackgroundService

// Test: LearningsPruningService_NotRegisteredWhenDisabled
//   Arrange: AppConfig { AI = { Learnings = { Enabled = false } } }
//   Act: resolve IHostedService collection
//   Assert: should NOT contain LearningsPruningBackgroundService

// --- Bridge DI Tests ---

// Test: DriftEscalationBridge_RegisteredAsNotificationChannel
//   Arrange: base setup
//   Act: resolve IEnumerable<IEscalationNotificationChannel>
//   Assert: should contain an instance of DriftEscalationBridge

// --- Config Binding Tests ---

// Test: AIConfig_BindsDriftDetectionConfig
//   Arrange: construct AppConfig with AI.DriftDetection populated
//   Assert: config.AI.DriftDetection is not null, has expected defaults

// Test: AIConfig_BindsLearningsConfig
//   Arrange: construct AppConfig with AI.Learnings populated
//   Assert: config.AI.Learnings is not null, has expected defaults
```

**Test infrastructure note:** Tests that need `IKnowledgeGraphStore` should register the `"in_memory"` keyed implementation either via `AddKnowledgeGraphDependencies` or a manual `services.AddSingleton<IKnowledgeGraphStore>(new InMemoryGraphStore(...))` stub to keep tests fast and isolated.

## Implementation Details

### 1. Update AIConfig.cs

**File:** `src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs`

Add two new properties alongside the existing `Governance` property:

```csharp
/// <summary>
/// Drift detection configuration: EWMA scoring, control limits, severity thresholds,
/// and escalation integration settings.
/// </summary>
public DriftDetectionConfig DriftDetection { get; init; } = new();

/// <summary>
/// Learnings log configuration: feedback blending, decay shelf lives, diversity injection,
/// and baseline adjustment thresholds.
/// </summary>
public LearningsConfig Learnings { get; init; } = new();
```

Add corresponding `using` directives for the config namespaces. The config classes themselves are created by sections 03 and 04; this section only adds them as properties on `AIConfig`.

Update the XML doc hierarchy comment at the top of `AIConfig` to include the two new entries:
```
/// -- DriftDetection   -- EWMA drift scoring, severity thresholds, escalation bridge
/// -- Learnings        -- Feedback blending, temporal decay, pruning, diversity injection
```

### 2. Update Infrastructure.AI DependencyInjection.cs

**File:** `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs`

Add a new private method `RegisterDriftDetectionServices` and a new private method `RegisterLearningsServices`, called from `AddInfrastructureAIDependencies` alongside the existing `RegisterEscalationServices` and `RegisterResilienceServices` calls.

**`RegisterDriftDetectionServices(services, appConfig)` registrations:**

| Interface | Implementation | Lifetime | Notes |
|-----------|---------------|----------|-------|
| `IDriftDetectionService` | `DefaultDriftDetectionService` | Singleton | Main orchestrator |
| `IDriftScorer` [keyed `"ewma"`] | `EwmaDriftScorer` | Singleton | EWMA scoring strategy |
| `IDriftBaselineStore` [keyed `"graph"`] | `GraphDriftBaselineStore` | Singleton | Graph-backed baselines |
| `IDriftBaselineStore` [keyed `"in_memory"`] | `InMemoryDriftBaselineStore` | Singleton | Testing/fallback |
| `IDriftBaselineStore` (default) | Resolved from config | Singleton | Reads `AppConfig.AI.DriftDetection` to select keyed impl. Default: `"graph"` |
| `IDriftAuditStore` | `JsonlDriftAuditStore` | Singleton | JSONL append-only audit |
| `IDriftNotifier` | `CompositeDriftNotifier` | Singleton | Fan-out to all `IDriftNotificationChannel` |
| `IEwmaStateStore` | `GraphEwmaStateStore` | Singleton | EWMA state persistence via knowledge graph |
| `IEscalationNotificationChannel` | `DriftEscalationBridge` | Singleton | **Added as additional channel** alongside existing NoOpSlack/NoOpTeams |

The `DriftEscalationBridge` must be registered as an `IEscalationNotificationChannel` (not replacing, but adding to the existing channels). The `CompositeEscalationNotifier` discovers all registered `IEscalationNotificationChannel` implementations via `IEnumerable<IEscalationNotificationChannel>`, so adding it is a simple `services.AddSingleton<IEscalationNotificationChannel, DriftEscalationBridge>()` line in the existing `RegisterEscalationServices` method.

**Default baseline store resolution pattern** (mirrors `IKnowledgeGraphStore` default resolution):
```csharp
services.AddSingleton<IDriftBaselineStore>(sp =>
{
    var config = sp.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;
    var provider = "graph";
    return sp.GetRequiredKeyedService<IDriftBaselineStore>(provider);
});
```

**`RegisterLearningsServices(services, appConfig)` registrations:**

| Interface | Implementation | Lifetime | Notes |
|-----------|---------------|----------|-------|
| `ILearningDecayService` | `DefaultLearningDecayService` | Singleton | Freshness + pruning |
| `LearningsPruningBackgroundService` | Itself | HostedService | **Conditional**: only when `appConfig.AI.Learnings.Enabled` is true |

The pruning background service follows the exact pattern from `RegisterResilienceServices`:
```csharp
if (appConfig.AI.Learnings.Enabled)
{
    services.AddSingleton<LearningsPruningBackgroundService>();
    services.AddHostedService(sp => sp.GetRequiredService<LearningsPruningBackgroundService>());
}
```

**Required usings to add:**
```csharp
using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.Learnings;
using Infrastructure.AI.DriftDetection;
using Infrastructure.AI.Learnings;
```

### 3. Update Infrastructure.AI.KnowledgeGraph DependencyInjection.cs

**File:** `src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/DependencyInjection.cs`

Add learnings store registrations after the existing procedural memory section:

| Interface | Implementation | Lifetime | Notes |
|-----------|---------------|----------|-------|
| `ILearningsStore` [keyed `"graph"`] | `GraphLearningsStore` | Singleton | Graph-backed with index nodes |
| `ILearningsStore` [keyed `"in_memory"`] | `InMemoryLearningsStore` | Singleton | Testing/fallback |
| `ILearningsStore` (default) | Resolved from config | Singleton | Reads `AppConfig.AI.Learnings.StoreProvider` (default: `"graph"`) |

**Default resolution pattern:**
```csharp
services.AddSingleton<ILearningsStore>(sp =>
{
    var config = sp.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;
    var provider = config.AI.Learnings.StoreProvider;
    return sp.GetRequiredKeyedService<ILearningsStore>(provider);
});
```

**Required usings to add:**
```csharp
using Application.AI.Common.Interfaces.Learnings;
using Infrastructure.AI.KnowledgeGraph.Learnings;
```

### 4. Update Presentation.AgentHub DependencyInjection.cs

**File:** `src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs`

Add AG-UI notification channel registrations near the existing `AgUiEscalationNotifier` line (around line 186):

```csharp
// Drift detection AG-UI notifications
services.AddSingleton<IDriftNotificationChannel, AgUiDriftNotifier>();

// Learnings AG-UI notifications
services.AddSingleton<ILearningNotificationChannel, AgUiLearningNotifier>();
```

**Required usings to add:**
```csharp
using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.Learnings;
using Presentation.AgentHub.Notifications;
```

### 5. Composition Root Verification

**File:** `src/Content/Presentation/Presentation.Common/Extensions/IServiceCollectionExtensions.cs`

Verify that `AddKnowledgeGraphDependencies(appConfig)` is called in `AddGlobalProjectDependencies`. Currently it is NOT called there (the `IKnowledgeGraphStore` gets resolved from RAG's `AddRagDependencies`, but `GraphLearningsStore` and the learnings store keyed DI live in `Infrastructure.AI.KnowledgeGraph`). If it's not already called, it must be added:

```csharp
// Infrastructure layer
services.AddInfrastructureCommonDependencies();
services.AddKnowledgeGraphDependencies(appConfig);  // <-- NEW: must come before RAG
// RAG must register before Infrastructure.AI -- tool registrations depend on IRagOrchestrator
services.AddRagDependencies(appConfig);
services.AddInfrastructureAIDependencies(appConfig);
```

Knowledge graph must register before RAG because RAG resolves `IKnowledgeGraphStore` from the container. If it's already registered elsewhere (check at implementation time), skip this step.

### 6. MediatR Auto-Discovery

MediatR command handlers (`RememberCommandHandler`, `RecallQueryHandler`, etc. from section 13) and FluentValidation validators (`DriftConfigValidator`, `LearningsConfigValidator` from sections 03-04) are auto-discovered via assembly scanning. No explicit DI registration is needed for these -- the existing `AddApplicationCommonDependencies` call registers `MediatR` with assembly scanning and `FluentValidation` validators via `RegisterValidatorsFromAssemblyContaining`.

Verify that the assembly containing the command handlers (`Application.Core`) is already scanned. Check `Application.Core/DependencyInjection.cs` for the MediatR assembly registration.

## Actual Implementation

### Deviations from Plan

1. **AIConfig.cs already had DriftDetection and Learnings properties** — Added in sections 03-04. Step 1 (Update AIConfig.cs) was skipped entirely.

2. **Config section bindings pulled forward from section-19** — `Configure<DriftDetectionConfig>` and `Configure<LearningsConfig>` were added to `RegisterConfigSections` in this section because `DefaultLearningDecayService` takes `IOptionsMonitor<LearningsConfig>` (not `IOptionsMonitor<AppConfig>`). Section-19 can skip these bindings.

3. **Existing DependencyInjectionTests required updates** — The new drift/learnings registrations introduced transitive dependencies (`ISender` for `DriftEscalationBridge`, `TimeProvider`, `IKnowledgeGraphStore`) that the existing test helper didn't provide. Fixed by adding `TimeProvider.System`, `Mock<ISender>`, and `AddKnowledgeGraphDependencies` to `CreateBaseServices`.

4. **Hardcoded baseline store default** — Unlike `ILearningsStore` which resolves from `LearningsConfig.StoreProvider`, `IDriftBaselineStore` defaults to `"graph"` because `DriftDetectionConfig` has no `BaselineProvider` property. Documented with a comment explaining EWMA continuity requires persistent storage.

5. **Project references added** — `Presentation.Common.csproj` and `Infrastructure.AI.Tests.csproj` both needed `Infrastructure.AI.KnowledgeGraph` project references for the `AddKnowledgeGraphDependencies` extension method.

### Files Created/Modified

| File | Action |
|------|--------|
| `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs` | Added `RegisterDriftDetectionServices` + `RegisterLearningsServices`, added `DriftEscalationBridge` to escalation channels |
| `src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/DependencyInjection.cs` | Added keyed `ILearningsStore` registrations ("graph"/"in_memory" + config-driven default) |
| `src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs` | Added `AgUiDriftNotifier` and `AgUiLearningNotifier` channel registrations |
| `src/Content/Presentation/Presentation.Common/Extensions/IServiceCollectionExtensions.cs` | Added `AddKnowledgeGraphDependencies` to composition root + `Configure<DriftDetectionConfig>`/`Configure<LearningsConfig>` section bindings |
| `src/Content/Presentation/Presentation.Common/Presentation.Common.csproj` | Added project reference to Infrastructure.AI.KnowledgeGraph |
| `src/Content/Tests/Infrastructure.AI.Tests/DriftLearningsDiTests.cs` | New: 15 DI resolution tests |
| `src/Content/Tests/Infrastructure.AI.Tests/DependencyInjectionTests.cs` | Updated: added TimeProvider, ISender mock, KnowledgeGraph deps to test helper |
| `src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj` | Added project reference to Infrastructure.AI.KnowledgeGraph |

### Test Results

- DriftLearningsDiTests: 15/15 passed
- DependencyInjectionTests: 13/13 passed (no regressions)
- Total new + regression-verified: 28/28 passed

## Key Patterns to Follow

1. **Keyed DI with default resolution from config** -- matches `IKnowledgeGraphStore` pattern: register keyed implementations, then a non-keyed singleton that resolves the correct key from `IOptionsMonitor<AppConfig>`.
2. **Conditional hosted service registration** -- matches `LlmRetryQueue` pattern: check `appConfig.AI.Learnings.Enabled` before registering `LearningsPruningBackgroundService`.
3. **Composite notifier + individual channels** -- matches `CompositeEscalationNotifier` / `IEscalationNotificationChannel` pattern: `CompositeDriftNotifier` registered as `IDriftNotifier`, individual channels as `IDriftNotificationChannel`. The composite collects channels via `IEnumerable<IDriftNotificationChannel>`.
4. **Bridge as notification channel** -- `DriftEscalationBridge` is registered as `IEscalationNotificationChannel` (not `IDriftNotificationChannel`). It listens to escalation resolutions and bridges them back into drift/learnings. This is registered alongside the existing NoOp channels.
5. **Singleton lifetime for stateless services** -- all drift/learnings services are singleton, matching the existing Infrastructure.AI pattern. The `TimeProvider` and `IOptionsMonitor<AppConfig>` injections handle time and config changes respectively.

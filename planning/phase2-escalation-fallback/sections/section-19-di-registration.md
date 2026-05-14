# Section 19: DI Registration

## Overview

This section wires every service created in sections 1-18 into the DI containers across three layers: `Infrastructure.AI`, `Application.Core`, and `Presentation.AgentHub`. It also adds Options bindings for `EscalationConfig` and `ResilienceConfig` in the Presentation composition root, and conditionally registers the `LlmRetryQueue` hosted service only when resilience is enabled.

**Dependencies:** Sections 01-18 must be complete (all types, interfaces, and implementations exist). This section only adds DI registration lines -- no new types are created.

## Files to Modify

| File | What Changes |
|------|-------------|
| `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs` | Escalation services, resilience services, conditional hosted service |
| `src/Content/Application/Application.Core/DependencyInjection.cs` | Keyed approval strategies |
| `src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs` | AG-UI escalation notification channel |
| `src/Content/Presentation/Presentation.Common/Extensions/IServiceCollectionExtensions.cs` | Options bindings for `EscalationConfig` and `ResilienceConfig` |

## Tests First

Tests go in existing DI test files. They verify that service resolution succeeds after registration.

### File: `src/Content/Tests/Infrastructure.AI.Tests/DependencyInjectionTests.cs`

Add these test methods to the existing `DependencyInjectionTests` class:

```csharp
// Test: AddInfrastructureAIDependencies_RegistersIEscalationService
//   Arrange: CreateBaseServices(), add Infrastructure.AI deps
//   Act: Resolve IEscalationService
//   Assert: Should().NotBeNull() and be DefaultEscalationService

// Test: AddInfrastructureAIDependencies_RegistersIEscalationAuditStore
//   Arrange: CreateBaseServices(), add Infrastructure.AI deps
//   Act: Resolve IEscalationAuditStore
//   Assert: Should().NotBeNull() and be JsonlEscalationAuditStore

// Test: AddInfrastructureAIDependencies_RegistersIEscalationNotifier
//   Arrange: CreateBaseServices(), add Infrastructure.AI deps
//   Act: Resolve IEscalationNotifier
//   Assert: Should().NotBeNull() and be CompositeEscalationNotifier

// Test: AddInfrastructureAIDependencies_RegistersNotificationChannels
//   Arrange: CreateBaseServices(), add Infrastructure.AI deps
//   Act: Resolve IEnumerable<IEscalationNotificationChannel>
//   Assert: Should contain NoOpSlackNotifier and NoOpTeamsNotifier

// Test: AddInfrastructureAIDependencies_RegistersIProviderHealthMonitor
//   Arrange: CreateBaseServices(), add Infrastructure.AI deps
//   Act: Resolve IProviderHealthMonitor
//   Assert: Should().NotBeNull() and be PollyProviderHealthMonitor

// Test: AddInfrastructureAIDependencies_RegistersIResilientChatClientProvider
//   Arrange: CreateBaseServices(), add Infrastructure.AI deps
//   Act: Resolve IResilientChatClientProvider
//   Assert: Should().NotBeNull() and be ResilientChatClientProvider

// Test: AddInfrastructureAIDependencies_ResilienceEnabled_RegistersLlmRetryQueueHostedService
//   Arrange: CreateBaseServices() with AppConfig { AI = { Resilience = { Enabled = true } } }
//   Act: Resolve IEnumerable<IHostedService>
//   Assert: Should contain LlmRetryQueue

// Test: AddInfrastructureAIDependencies_ResilienceDisabled_DoesNotRegisterLlmRetryQueueHostedService
//   Arrange: CreateBaseServices() with AppConfig { AI = { Resilience = { Enabled = false } } }
//   Act: Resolve IEnumerable<IHostedService>
//   Assert: Should NOT contain LlmRetryQueue

// Test: CompositeNotifier_DoesNotContainItself
//   Arrange: Build provider with all escalation registrations
//   Act: Resolve IEscalationNotifier
//   Assert: The composite should not inject itself as a channel.
//   Verify by resolving IEnumerable<IEscalationNotificationChannel> -- none should be CompositeEscalationNotifier.
```

### File: `src/Content/Tests/Application.Core.Tests/DependencyInjectionTests.cs`

Add these test methods to the existing `DependencyInjectionTests` class:

```csharp
// Test: AddApplicationCoreDependencies_RegistersApprovalStrategies_KeyedByType
//   Arrange: BuildProvider()
//   Act: Resolve IApprovalStrategy keyed by ApprovalStrategyType.AnyOf
//   Assert: Should be AnyOfApprovalStrategy
//   Repeat for AllOf -> AllOfApprovalStrategy, Quorum -> QuorumApprovalStrategy

// Test: IApprovalStrategy_KeyedDI_ResolvesCorrectStrategy
//   Arrange: BuildProvider()
//   Act: For each ApprovalStrategyType enum value, resolve keyed IApprovalStrategy
//   Assert: Each returns the correct concrete type
```

### File: `src/Content/Tests/Presentation.Common.Tests/Extensions/IServiceCollectionExtensionsTests.cs`

Add tests verifying Options bindings:

```csharp
// Test: RegisterConfigSections_BindsEscalationConfig
//   Arrange: Build IConfiguration with AppConfig:AI:Governance:Escalation section
//   Act: RegisterConfigSections, resolve IOptionsMonitor<EscalationConfig>
//   Assert: CurrentValue reflects bound values

// Test: RegisterConfigSections_BindsResilienceConfig
//   Arrange: Build IConfiguration with AppConfig:AI:Resilience section
//   Act: RegisterConfigSections, resolve IOptionsMonitor<ResilienceConfig>
//   Assert: CurrentValue reflects bound values
```

## Implementation Details

### 1. Infrastructure.AI `DependencyInjection.cs`

Add two new private methods and call them from `AddInfrastructureAIDependencies`.

**New method: `RegisterEscalationServices`**

```csharp
/// <summary>
/// Registers escalation pipeline services: service, audit store, composite notifier,
/// and no-op notification channel stubs.
/// </summary>
private static void RegisterEscalationServices(IServiceCollection services)
{
    // Escalation orchestrator -- singleton because it holds in-memory ConcurrentDictionary state
    services.AddSingleton<IEscalationService, DefaultEscalationService>();

    // Audit store -- append-only JSONL, singleton for file handle reuse
    services.AddSingleton<IEscalationAuditStore, JsonlEscalationAuditStore>();

    // Composite notifier -- fans out to all IEscalationNotificationChannel registrations
    services.AddSingleton<IEscalationNotifier, CompositeEscalationNotifier>();

    // No-op notification channel stubs -- extension points for template consumers
    services.AddSingleton<IEscalationNotificationChannel, NoOpSlackNotifier>();
    services.AddSingleton<IEscalationNotificationChannel, NoOpTeamsNotifier>();
}
```

Key design point: `CompositeEscalationNotifier` is registered as `IEscalationNotifier`. The individual channel implementations are registered as `IEscalationNotificationChannel`. The composite injects `IEnumerable<IEscalationNotificationChannel>` -- it never receives itself because the interfaces are different. This prevents infinite recursion.

**New method: `RegisterResilienceServices`**

```csharp
/// <summary>
/// Registers resilience pipeline services: health monitor, resilient provider,
/// and conditionally the retry queue hosted service.
/// </summary>
private static void RegisterResilienceServices(IServiceCollection services, AppConfig appConfig)
{
    // Health monitor -- singleton, holds circuit breaker state references
    // Double registration: concrete type for ReportStateChange access + interface for consumers
    services.AddSingleton<PollyProviderHealthMonitor>();
    services.AddSingleton<IProviderHealthMonitor>(sp => sp.GetRequiredService<PollyProviderHealthMonitor>());

    // Capability registry -- config-driven, singleton
    services.AddSingleton<ProviderCapabilityRegistry>();

    // Resilient chat client provider -- singleton, caches the composed fallback chain
    services.AddSingleton<IResilientChatClientProvider, ResilientChatClientProvider>();

    // Retry queue -- only registered when resilience is enabled
    if (appConfig.AI.Resilience.Enabled)
    {
        services.AddSingleton<LlmRetryQueue>();
        services.AddHostedService(sp => sp.GetRequiredService<LlmRetryQueue>());
    }
}
```

The conditional `LlmRetryQueue` check uses the materialized `appConfig` object. The double registration for `PollyProviderHealthMonitor` ensures both `IProviderHealthMonitor` (for consumers) and the concrete type (for `ProviderResiliencePipelineBuilder` which needs `ReportStateChange`) resolve to the same singleton.

**Call sites in `AddInfrastructureAIDependencies`:**

```csharp
// Escalation pipeline -- service, audit, composite notifier, channel stubs
RegisterEscalationServices(services);

// Resilience pipeline -- health monitor, resilient provider, conditional retry queue
RegisterResilienceServices(services, appConfig);
```

### 2. Application.Core `DependencyInjection.cs`

Add keyed DI registrations for the three approval strategies:

```csharp
// Approval strategies -- keyed by ApprovalStrategyType for IEscalationService to resolve
services.AddKeyedSingleton<IApprovalStrategy>(ApprovalStrategyType.AnyOf, (_, _) => new AnyOfApprovalStrategy());
services.AddKeyedSingleton<IApprovalStrategy>(ApprovalStrategyType.AllOf, (_, _) => new AllOfApprovalStrategy());
services.AddKeyedSingleton<IApprovalStrategy>(ApprovalStrategyType.Quorum, (_, _) => new QuorumApprovalStrategy());
```

The keyed DI pattern mirrors the existing `ISupervisorStrategy` keyed registration. The key is the `ApprovalStrategyType` enum value. Consumers resolve via `IServiceProvider.GetRequiredKeyedService<IApprovalStrategy>(request.ApprovalStrategy)`.

### 3. Presentation.AgentHub `DependencyInjection.cs`

Add the AG-UI escalation notification channel and writer accessor:

```csharp
// Writer accessor -- singleton with AsyncLocal storage
services.AddSingleton<IAgUiEventWriterAccessor, AgUiEventWriterAccessor>();

// AG-UI escalation notifications -- pushes escalation events through the SSE stream
services.AddSingleton<IEscalationNotificationChannel, AgUiEscalationNotifier>();
```

### 4. Presentation.Common `RegisterConfigSections`

Add explicit Options bindings for the new config classes:

```csharp
services.Configure<EscalationConfig>(configuration.GetSection("AppConfig:AI:Governance:Escalation"));
services.Configure<ResilienceConfig>(configuration.GetSection("AppConfig:AI:Resilience"));
```

This enables `IOptionsMonitor<EscalationConfig>` and `IOptionsMonitor<ResilienceConfig>` to resolve correctly throughout the application.

---

## Registration Summary

| Interface | Implementation | Lifetime | Layer | Key |
|-----------|---------------|----------|-------|-----|
| `IEscalationService` | `DefaultEscalationService` | Singleton | Infrastructure.AI | -- |
| `IEscalationAuditStore` | `JsonlEscalationAuditStore` | Singleton | Infrastructure.AI | -- |
| `IEscalationNotifier` | `CompositeEscalationNotifier` | Singleton | Infrastructure.AI | -- |
| `IEscalationNotificationChannel` | `NoOpSlackNotifier` | Singleton | Infrastructure.AI | -- |
| `IEscalationNotificationChannel` | `NoOpTeamsNotifier` | Singleton | Infrastructure.AI | -- |
| `IEscalationNotificationChannel` | `AgUiEscalationNotifier` | Singleton | Presentation.AgentHub | -- |
| `IApprovalStrategy` | `AnyOfApprovalStrategy` | Singleton | Application.Core | `ApprovalStrategyType.AnyOf` |
| `IApprovalStrategy` | `AllOfApprovalStrategy` | Singleton | Application.Core | `ApprovalStrategyType.AllOf` |
| `IApprovalStrategy` | `QuorumApprovalStrategy` | Singleton | Application.Core | `ApprovalStrategyType.Quorum` |
| `PollyProviderHealthMonitor` | `PollyProviderHealthMonitor` | Singleton | Infrastructure.AI | -- |
| `IProviderHealthMonitor` | forwarded to `PollyProviderHealthMonitor` | Singleton | Infrastructure.AI | -- |
| `ProviderCapabilityRegistry` | `ProviderCapabilityRegistry` | Singleton | Infrastructure.AI | -- |
| `IResilientChatClientProvider` | `ResilientChatClientProvider` | Singleton | Infrastructure.AI | -- |
| `IHostedService` | `LlmRetryQueue` | Singleton | Infrastructure.AI | Conditional |
| `IAgUiEventWriterAccessor` | `AgUiEventWriterAccessor` | Singleton | Presentation.AgentHub | -- |

---

## Design Decisions

1. **Why singleton for `DefaultEscalationService`:** It manages in-memory `ConcurrentDictionary<Guid, EscalationState>` for active escalations. Multiple instances would fragment the state.

2. **Why conditional `LlmRetryQueue` instead of runtime check:** A hosted service that starts, runs a background loop, and consumes resources should not exist when resilience is disabled.

3. **Why keyed DI for approval strategies:** Matches the existing `ISupervisorStrategy` pattern. `DefaultEscalationService` resolves the correct strategy at runtime.

4. **Why explicit Options bindings for sub-configs:** .NET's `Configure<T>` does not automatically create child bindings. `IOptionsMonitor<EscalationConfig>` and `IOptionsMonitor<ResilienceConfig>` need their own `Configure<>` calls.

5. **Why double registration for `PollyProviderHealthMonitor`:** `IProviderHealthMonitor` is for consumers. The concrete type is for `ResilientChatClientProvider` which needs `ReportStateChange()`.

---

## Verification

```
dotnet build src/AgenticHarness.slnx
dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~DependencyInjectionTests"
dotnet test src/Content/Tests/Application.Core.Tests/Application.Core.Tests.csproj --filter "FullyQualifiedName~DependencyInjectionTests"
dotnet test src/Content/Tests/Presentation.Common.Tests/Presentation.Common.Tests.csproj --filter "FullyQualifiedName~IServiceCollectionExtensionsTests"
```

---

## Implementation Notes (Post-Implementation)

### Deviations from Plan

1. **Triple registration for LlmRetryQueue** — Plan showed concrete + IHostedService. Code review identified missing `ILlmRetryQueue` interface registration needed for Application-layer consumers. Added forwarding registration: `services.AddSingleton<ILlmRetryQueue>(sp => sp.GetRequiredService<LlmRetryQueue>())`.

2. **TimeProvider.System in tests** — `LlmRetryQueue` depends on `TimeProvider` which is not part of the base `CreateBaseServices()` helper. Added `services.AddSingleton(TimeProvider.System)` to the resilience-enabled test.

### Test Counts
- Infrastructure.AI DI: 9 new tests (escalation service, audit store, notifier, channels, health monitor, resilient provider, retry queue enabled/disabled, composite self-injection)
- Application.Core DI: 3 new tests (keyed approval strategies via Theory)
- Presentation.Common: 2 new tests (EscalationConfig binding, ResilienceConfig binding)
- Total new: 14 tests, all passing

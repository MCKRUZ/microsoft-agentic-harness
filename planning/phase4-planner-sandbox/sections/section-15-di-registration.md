# Section 15: DI Registration

## Overview

This section wires all Phase 4 Planner and Sandbox services into the dependency injection container across the appropriate layer-specific `DependencyInjection.cs` files. It follows the existing convention where each layer provides an `Add*Dependencies()` extension method called from the Presentation composition root in `AddGlobalProjectDependencies()`.

The registrations span four files:
- `Infrastructure.AI/DependencyInjection.cs` -- bulk of planner, sandbox, and attestation services
- `Presentation.AgentHub/DependencyInjection.cs` -- plan progress notifier (Application interface, Presentation implementation)
- `Presentation.Common/Extensions/IServiceCollectionExtensions.cs` -- composition root call ordering
- `Application.Core/DependencyInjection.cs` -- MediatR handler and FluentValidation auto-discovery (already handles new commands/validators via assembly scanning, but verify)

**Dependencies**: Sections 01-14 must be complete (all types, interfaces, implementations, commands, events exist before wiring).

**Blocks**: Section 16 (Configuration) -- options binding must reference already-registered services.

---

## Tests FIRST

All tests go in `src/Content/Tests/Infrastructure.AI.Tests/Planner/PlannerDiRegistrationTests.cs` following the existing `DependencyInjectionTests.cs` and `DriftLearningsDiTests.cs` patterns.

### Test File: `src/Content/Tests/Infrastructure.AI.Tests/Planner/PlannerDiRegistrationTests.cs`

```csharp
// Test: DependencyInjection_AllPlannerServices_Resolvable
//   Build a ServiceCollection with all required upstream dependencies mocked/stubbed,
//   call AddInfrastructureAIDependencies (which now includes planner registrations),
//   then resolve IPlanExecutor, IPlanValidator, IPlanGenerator, IPlanStateStore.
//   Assert all are non-null and the correct concrete types.

// Test: DependencyInjection_AllSandboxServices_Resolvable
//   Same setup. Resolve IAttestationService, ICapabilityEnforcer.
//   Assert non-null, correct concrete types.

// Test: DependencyInjection_KeyedStepExecutors_ResolveAllFiveTypes
//   Build the container. For each StepType enum value (LlmCall, ToolUse, HumanGate,
//   ConditionalBranch, SubPlanInvocation), resolve the keyed IPlanStepExecutor.
//   Assert each resolves to the correct concrete executor type.

// Test: DependencyInjection_KeyedSandboxExecutors_ResolveBothTiers
//   Resolve keyed ISandboxExecutor with SandboxIsolationLevel.Process => ProcessSandboxExecutor.
//   Resolve keyed ISandboxExecutor with SandboxIsolationLevel.Container => DockerSandboxExecutor.

// Test: DependencyInjection_PlannerDbContext_ScopedLifetime
//   Create a scope, resolve PlannerDbContext. Create a second scope, resolve again.
//   Assert the two instances are different (scoped lifetime proof).

// Test: DependencyInjection_DbContextFactory_AvailableForSingletons
//   Resolve IDbContextFactory<PlannerDbContext>. Call CreateDbContextAsync().
//   Assert the returned context is non-null.
```

### Additional Test File: `src/Content/Tests/Presentation.AgentHub.Tests/Planner/PlanProgressNotifierDiTests.cs`

```csharp
// Test: AddAgentHubServices_RegistersIPlanProgressNotifier
//   Resolve IPlanProgressNotifier from the service provider.
//   Assert it is non-null and of type AgUiPlanProgressNotifier.
```

---

## Implementation Details

### 1. Infrastructure.AI DI -- Planner and Sandbox Registrations

**File**: `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs`

Add two new private methods called from `AddInfrastructureAIDependencies`.

#### RegisterPlannerServices

```csharp
private static void RegisterPlannerServices(IServiceCollection services)
{
    // Core planner services -- scoped to match EF Core DbContext lifetime
    services.AddScoped<IPlanExecutor, PlanExecutor>();
    services.AddScoped<IPlanValidator, PlanValidator>();
    services.AddScoped<IPlanGenerator, LlmPlanGeneratorService>();
    services.AddScoped<IPlanStateStore, EfCorePlanStateStore>();

    // Step executors -- keyed by StepType enum
    services.AddKeyedScoped<IPlanStepExecutor>(StepType.LlmCall,
        (sp, _) => sp.GetRequiredService<LlmCallStepExecutor>());
    services.AddKeyedScoped<IPlanStepExecutor>(StepType.ToolUse,
        (sp, _) => sp.GetRequiredService<ToolUseStepExecutor>());
    services.AddKeyedScoped<IPlanStepExecutor>(StepType.HumanGate,
        (sp, _) => sp.GetRequiredService<HumanGateStepExecutor>());
    services.AddKeyedScoped<IPlanStepExecutor>(StepType.ConditionalBranch,
        (sp, _) => sp.GetRequiredService<ConditionalBranchStepExecutor>());
    services.AddKeyedScoped<IPlanStepExecutor>(StepType.SubPlanInvocation,
        (sp, _) => sp.GetRequiredService<SubPlanStepExecutor>());

    // Concrete executor types for keyed factory resolution
    services.AddScoped<LlmCallStepExecutor>();
    services.AddScoped<ToolUseStepExecutor>();
    services.AddScoped<HumanGateStepExecutor>();
    services.AddScoped<ConditionalBranchStepExecutor>();
    services.AddScoped<SubPlanStepExecutor>();
}
```

#### RegisterSandboxServices

```csharp
private static void RegisterSandboxServices(IServiceCollection services)
{
    // Sandbox executors -- keyed by SandboxIsolationLevel
    services.AddKeyedScoped<ISandboxExecutor>(SandboxIsolationLevel.Process,
        (sp, _) => sp.GetRequiredService<ProcessSandboxExecutor>());
    services.AddKeyedScoped<ISandboxExecutor>(SandboxIsolationLevel.Container,
        (sp, _) => sp.GetRequiredService<DockerSandboxExecutor>());

    services.AddScoped<ProcessSandboxExecutor>();
    services.AddScoped<DockerSandboxExecutor>();

    // Attestation
    services.AddScoped<IAttestationService, HmacAttestationService>();

    // Process resource limiter -- platform-specific, singleton
    if (OperatingSystem.IsWindows())
        services.AddSingleton<IProcessResourceLimiter, WindowsProcessResourceLimiter>();
    else
        services.AddSingleton<IProcessResourceLimiter, NoOpProcessResourceLimiter>();
}
```

#### EF Core Registration

Add to `AddInfrastructureAIDependencies` before the planner/sandbox registration calls:

```csharp
var plannerConnectionString = appConfig.Planner?.ConnectionString
    ?? $"DataSource={Path.Combine(AppContext.BaseDirectory, "data", "planner.db")}";
services.AddDbContext<PlannerDbContext>(options =>
    options.UseSqlite(plannerConnectionString));
services.AddDbContextFactory<PlannerDbContext>(options =>
    options.UseSqlite(plannerConnectionString), ServiceLifetime.Scoped);
```

#### Call Order

Add at the end of `AddInfrastructureAIDependencies`, after `RegisterLearningsServices`:

```csharp
RegisterPlannerServices(services);
RegisterSandboxServices(services);
```

EF Core must come before planner services (EfCorePlanStateStore depends on PlannerDbContext).

### 2. Presentation.AgentHub DI -- Plan Progress Notifier

**File**: `src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs`

Add near existing notification channel registrations:

```csharp
services.AddSingleton<IPlanProgressNotifier, AgUiPlanProgressNotifier>();
```

Singleton lifetime matches `AgUiDriftNotifier`, `AgUiEscalationNotifier`, `AgUiLearningNotifier`.

### 3. Composition Root

No changes needed to `AddGlobalProjectDependencies`. Planner/sandbox services registered inside `AddInfrastructureAIDependencies` which is already called. Current call order is correct:

1. `AddApplicationCoreDependencies` (MediatR handlers, validators via assembly scanning)
2. ... intermediate registrations ...
3. `AddInfrastructureAIDependencies` (planner/sandbox implementations)

### 4. Application.Core DI -- Automatic Discovery

No changes needed. Existing `AddMediatR` and `AddValidatorsFromAssembly` calls auto-discover the new planner command handlers and validators from Section 13.

### 5. NuGet Package Additions

**File**: `src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj`

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" PrivateAssets="All" />
<PackageReference Include="Docker.DotNet" />
```

### 6. Startup Migration

In the Presentation host's `Program.cs`, after `builder.Build()`:

```csharp
if (app.Configuration.GetValue("Planner:AutoMigrate", defaultValue: true))
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
    await dbContext.Database.MigrateAsync();
}
```

---

## Registration Summary Table

| Interface | Concrete Type | Lifetime | DI File | Pattern |
|-----------|--------------|----------|---------|---------|
| `IPlanExecutor` | `PlanExecutor` | Scoped | Infrastructure.AI | Direct |
| `IPlanValidator` | `PlanValidator` | Scoped | Infrastructure.AI | Direct |
| `IPlanGenerator` | `LlmPlanGeneratorService` | Scoped | Infrastructure.AI | Direct |
| `IPlanStateStore` | `EfCorePlanStateStore` | Scoped | Infrastructure.AI | Direct |
| `IPlanStepExecutor` (x5) | Per-StepType executors | Keyed Scoped | Infrastructure.AI | `AddKeyedScoped` by `StepType` |
| `ISandboxExecutor` (x2) | Process/Docker executors | Keyed Scoped | Infrastructure.AI | `AddKeyedScoped` by `SandboxIsolationLevel` |
| `IAttestationService` | `HmacAttestationService` | Scoped | Infrastructure.AI | Direct |
| `IProcessResourceLimiter` | Windows/NoOp | Singleton | Infrastructure.AI | Runtime OS branch |
| `PlannerDbContext` | -- | Scoped | Infrastructure.AI | `AddDbContext` |
| `IDbContextFactory<PlannerDbContext>` | -- | Scoped | Infrastructure.AI | `AddDbContextFactory` |
| `IPlanProgressNotifier` | `AgUiPlanProgressNotifier` | Singleton | Presentation.AgentHub | Direct |

---

## Files Created/Modified (Actual)

| File | Action |
|------|--------|
| `Infrastructure.AI/DependencyInjection.cs` | Modified -- added `RegisterPlannerDbContext`, `RegisterPlannerServices`, `RegisterSandboxServices` methods with IDockerClient default registration |
| `Tests/Infrastructure.AI.Tests/Planner/PlannerDiRegistrationTests.cs` | Created -- 6 DI resolution tests |

### Deviations from Plan
- `Presentation.AgentHub/DependencyInjection.cs` — already had `IPlanProgressNotifier` registered (done in Section 14). No changes needed.
- `Infrastructure.AI.csproj` — NuGet packages already present from prior sections. No changes needed.
- `PlanProgressNotifierDiTests.cs` — not created since registration already existed and is covered by existing AgentHub tests.
- DbContext uses factory-only pattern (review fix M2): `AddDbContextFactory` + scoped resolution via factory instead of dual `AddDbContext`+`AddDbContextFactory`.
- Added `Directory.CreateDirectory` for SQLite data path (review fix M1).
- Added `IDockerClient` singleton registration with default Docker endpoint (review fix H1).

---

## Key Constraints

1. **Keyed DI enum keys**: `AddKeyedScoped<IPlanStepExecutor>(StepType.LlmCall, ...)` uses the enum value as the key. Resolver calls `provider.GetRequiredKeyedService<IPlanStepExecutor>(step.Type)`.

2. **DbContext lifetime**: Singletons must use `IDbContextFactory<PlannerDbContext>` to create short-lived contexts. Direct `PlannerDbContext` injection only for scoped services.

3. **Registration order**: EF Core -> Planner services -> Sandbox services. Planner before sandbox because `ToolUseStepExecutor` depends on `ISandboxExecutor`.

4. **Docker.DotNet availability**: `DockerSandboxExecutor` constructor should not throw if Docker daemon is unavailable. Runtime check happens at execution time.

5. **Assembly scanning coverage**: Verify all command handlers/validators are in the `Application.Core` assembly to be auto-discovered by MediatR scanning.

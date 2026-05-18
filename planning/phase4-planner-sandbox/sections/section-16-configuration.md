# Section 16: Configuration -- PlannerOptions, SandboxOptions, and appsettings Binding

## Overview

This section defines the strongly-typed options classes (`PlannerOptions`, `SandboxOptions`) for the Planner and Sandbox subsystems, adds the corresponding `appsettings.json` sections to both Presentation hosts (AgentHub and ConsoleUI), and wires them via `IOptionsMonitor<T>` in DI. This is the final section in the implementation order -- it depends on Section 15 (DI Registration) being complete so that all services consuming these options are already registered.

## Dependencies

- **Section 01 (Domain Models)**: `SandboxIsolationLevel` enum used in `SandboxOptions`
- **Section 15 (DI Registration)**: All services that consume `PlannerOptions` / `SandboxOptions` must already be registered

---

## Tests FIRST

### File: `src/Content/Tests/Infrastructure.AI.Tests/Configuration/PlannerOptionsTests.cs`

```csharp
// Test: PlannerOptions_Binding_ReadsFromAppSettings
//   Arrange: Build IConfiguration from in-memory dictionary with Planner section keys
//   Act: Bind to PlannerOptions via configuration.GetSection("AppConfig:AI:Planner").Get<PlannerOptions>()
//   Assert: All properties match the dictionary values

// Test: PlannerOptions_Defaults_MaxConcurrentPlans50_MaxParallelSteps10
//   Arrange: new PlannerOptions() -- no configuration
//   Assert: MaxConcurrentPlans == 50, MaxParallelSteps == 10, PlanTimeoutMinutes == 30,
//           MaxSubPlanDepth == 5, AutoMigrate == true, DatabasePath == "data/planner.db"
```

### File: `src/Content/Tests/Infrastructure.AI.Tests/Configuration/SandboxOptionsTests.cs`

```csharp
// Test: SandboxOptions_Binding_ReadsFromAppSettings
//   Arrange: Build IConfiguration from in-memory dictionary with Sandbox section keys
//   Act: Bind to SandboxOptions
//   Assert: All properties match including nested ToolOverrides and ContainerDefaults

// Test: SandboxOptions_ToolOverrides_ParsedCorrectly
//   Arrange: In-memory config with Sandbox:ToolOverrides:file_system section
//   Act: Bind to SandboxOptions
//   Assert: ToolOverrides["file_system"].DeniedCapabilities contains "NetworkAccess",
//           AllowedPaths contains "./workspace", MinimumIsolation == "Process"

// Test: SandboxOptions_Defaults_ProcessIsolation_256MbMemory
//   Arrange: new SandboxOptions() -- no configuration
//   Assert: DefaultIsolationLevel == SandboxIsolationLevel.Process,
//           DefaultMemoryLimitMb == 256, DefaultCpuTimeSeconds == 30,
//           DefaultMaxSubprocesses == 5, DefaultDiskQuotaMb == 100,
//           DefaultTimeoutSeconds == 60, ContainerDefaults.DefaultImage is non-empty,
//           ContainerDefaults.NetworkMode == "none"
```

---

## Implementation Details

### 1. PlannerOptions

**File**: `src/Content/Domain/Domain.Common/Config/AI/Planner/PlannerOptions.cs`

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| `Enabled` | `bool` | `true` | Master toggle for planner subsystem |
| `MaxConcurrentPlans` | `int` | `50` | Maximum plans executing simultaneously |
| `MaxParallelSteps` | `int` | `10` | Max concurrent steps within a single plan (feeds `SemaphoreSlim`) |
| `PlanTimeoutMinutes` | `int` | `30` | Default plan-level timeout |
| `MaxSubPlanDepth` | `int` | `5` | Maximum sub-plan nesting depth |
| `AutoMigrate` | `bool` | `true` | Apply EF Core migrations at startup (false in production) |
| `DatabasePath` | `string` | `"data/planner.db"` | SQLite database file path relative to `AppContext.BaseDirectory` |
| `CheckpointAfterEachStep` | `bool` | `true` | Persist state after every step transition |

### 2. SandboxOptions

**File**: `src/Content/Domain/Domain.Common/Config/AI/Sandbox/SandboxOptions.cs`

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| `Enabled` | `bool` | `true` | Master toggle for sandbox subsystem |
| `DefaultIsolationLevel` | `SandboxIsolationLevel` | `Process` | Default isolation when tool has no explicit minimum |
| `DefaultMemoryLimitMb` | `int` | `256` | Default memory limit in MB |
| `DefaultCpuTimeSeconds` | `double` | `30` | Default CPU time limit |
| `DefaultMaxSubprocesses` | `int` | `5` | Default max child processes per tool execution |
| `DefaultDiskQuotaMb` | `int` | `100` | Default disk quota in MB for workspace directories |
| `DefaultTimeoutSeconds` | `int` | `60` | Default execution timeout |
| `ContainerDefaults` | `ContainerDefaultsConfig` | (see below) | Docker container defaults |
| `ToolOverrides` | `Dictionary<string, ToolOverrideConfig>` | empty | Per-tool capability/isolation overrides |

### 3. ContainerDefaultsConfig

**File**: `src/Content/Domain/Domain.Common/Config/AI/Sandbox/ContainerDefaultsConfig.cs`

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| `DefaultImage` | `string` | `"mcr.microsoft.com/dotnet/runtime:10.0-alpine"` | Default Docker image |
| `NetworkMode` | `string` | `"none"` | Default Docker network mode |
| `ReadonlyRootfs` | `bool` | `true` | Mount root filesystem read-only |
| `AutoRemove` | `bool` | `true` | Auto-remove container after exit |
| `WorkspaceMountPath` | `string` | `"/workspace"` | Container-side mount path |
| `KillGracePeriodSeconds` | `int` | `10` | Grace period before hard-killing timed-out container |

### 4. ToolOverrideConfig

**File**: `src/Content/Domain/Domain.Common/Config/AI/Sandbox/ToolOverrideConfig.cs`

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| `DeniedCapabilities` | `List<string>` | empty | Capability names to deny |
| `AllowedPaths` | `List<string>` | empty | Filesystem paths the tool may access |
| `DeniedPaths` | `List<string>` | empty | Filesystem paths explicitly denied (overrides AllowedPaths) |
| `AllowedHosts` | `List<string>` | empty | Network hosts the tool may contact |
| `DeniedHosts` | `List<string>` | empty | Network hosts explicitly denied |
| `MinimumIsolation` | `string?` | `null` | Isolation level override (parsed to `SandboxIsolationLevel`) |
| `MemoryLimitMb` | `int?` | `null` | Per-tool memory limit override |
| `CpuTimeSeconds` | `double?` | `null` | Per-tool CPU time override |
| `TimeoutSeconds` | `int?` | `null` | Per-tool execution timeout override |

### 5. Wire into AIConfig

**File to modify**: `src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs`

Add two new properties:

```csharp
public PlannerOptions Planner { get; set; } = new();
public SandboxOptions Sandbox { get; set; } = new();
```

### 6. appsettings.json Sections

**Files to modify**: Both `Presentation.AgentHub/appsettings.json` and `Presentation.ConsoleUI/appsettings.json`

Add under `"AppConfig" > "AI"`:

```json
"Planner": {
  "Enabled": true,
  "MaxConcurrentPlans": 50,
  "MaxParallelSteps": 10,
  "PlanTimeoutMinutes": 30,
  "MaxSubPlanDepth": 5,
  "AutoMigrate": true,
  "DatabasePath": "data/planner.db",
  "CheckpointAfterEachStep": true
},
"Sandbox": {
  "Enabled": true,
  "DefaultIsolationLevel": "Process",
  "DefaultMemoryLimitMb": 256,
  "DefaultCpuTimeSeconds": 30,
  "DefaultMaxSubprocesses": 5,
  "DefaultDiskQuotaMb": 100,
  "DefaultTimeoutSeconds": 60,
  "ContainerDefaults": {
    "DefaultImage": "mcr.microsoft.com/dotnet/runtime:10.0-alpine",
    "NetworkMode": "none",
    "ReadonlyRootfs": true,
    "AutoRemove": true,
    "WorkspaceMountPath": "/workspace",
    "KillGracePeriodSeconds": 10
  },
  "ToolOverrides": {}
}
```

### 7. Options Binding

No additional DI registration needed. `PlannerOptions` and `SandboxOptions` are nested properties on `AIConfig`, which is automatically bound when `services.Configure<AppConfig>(configuration.GetSection("AppConfig"))` is called in the Presentation composition root.

Services consume configuration via `IOptionsMonitor<AppConfig>` and navigate to `appConfig.AI.Planner` or `appConfig.AI.Sandbox`.

---

## Files Created/Modified (Actual)

| File | Action |
|------|--------|
| `Domain.Common/Config/AI/Planner/PlannerOptions.cs` | Created -- 8 properties with defaults |
| `Domain.Common/Config/AI/Sandbox/SandboxOptions.cs` | Created -- resource limits, container defaults, tool overrides |
| `Domain.Common/Config/AI/Sandbox/ContainerDefaultsConfig.cs` | Created -- Docker container defaults |
| `Domain.Common/Config/AI/Sandbox/ToolOverrideConfig.cs` | Modified -- added MemoryLimitMb, CpuTimeSeconds, TimeoutSeconds |
| `Domain.Common/Config/AI/AIConfig.cs` | Modified -- added Planner/Sandbox properties + using directives |
| `Presentation.AgentHub/appsettings.json` | Modified -- added Planner and Sandbox config sections |
| `Presentation.ConsoleUI/appsettings.json` | Modified -- added Planner and Sandbox config sections |
| `Infrastructure.AI/DependencyInjection.cs` | Modified -- RegisterPlannerDbContext reads DatabasePath from config |
| `Tests/Infrastructure.AI.Tests/Configuration/PlannerOptionsTests.cs` | Created -- 2 tests |
| `Tests/Infrastructure.AI.Tests/Configuration/SandboxOptionsTests.cs` | Created -- 3 tests |

### Deviations from Plan
- `SandboxOptions.DefaultIsolationLevel` uses `string` (not enum) to avoid Domain.Common -> Domain.AI dependency direction violation. Same pattern as SandboxConfig.DefaultGrantedCapabilities from Section 06.
- `ToolOverrideConfig.cs` was modified (not created) -- it already existed from Section 06. Added 3 resource limit fields.
- `RegisterPlannerDbContext` wired to read `appConfig.AI.Planner.DatabasePath` instead of hardcoding (review fix M3).
- No separate IOptionsMonitor<> registration needed -- bound as nested properties on AIConfig.

---

## Verification

```powershell
dotnet build src/AgenticHarness.slnx
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Infrastructure.AI.Tests.Configuration"
```

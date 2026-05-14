# Section 8: DI Registration & Configuration

## Overview

This section wires all Phase 1 components together: DI registrations in both `Infrastructure.AI` and `Application.Core`, new configuration POCOs for autonomy tier policies and capability match weights, extensions to existing config classes (`PermissionsConfig` and `SubagentConfig`), OTel metric instruments, and an example `appsettings.json` block. After this section, all services from sections 1-7 are resolvable from the DI container.

## Dependencies

- **Section 01** (Domain: Autonomy) -- `AutonomyLevel` enum, `AutonomyTierPolicy`, `AutonomyExceededResult` records
- **Section 02** (Domain: Delegation) -- `DelegationRecord`, `DelegationResult`, `DelegationState`, `SupervisorDecisionContext`, `AgentCandidate`, `AgentSelection`, `CapabilityScore` records
- **Section 03** (Interfaces) -- `IAutonomyTierResolver`, `ISupervisor`, `ISupervisorStrategy`, `IDelegationStore`
- **Section 04** (Tier Rule Provider) -- `AutonomyTierRuleProvider` implementation
- **Section 05** (Capability Strategy) -- `CapabilityMatchStrategy` implementation
- **Section 06** (JSONL Store) -- `JsonlDelegationStore` implementation
- **Section 07** (Supervisor) -- `CapabilityMatchSupervisor` implementation

All of these must be buildable before this section can compile.

---

## Tests (implement first)

Test file: `src/Content/Tests/Infrastructure.AI.Tests/DependencyInjectionTests.cs` (extend existing file)

Add the following test methods to the existing `DependencyInjectionTests` class. The class already has a `CreateBaseServices()` helper and an `OptionsMonitorStub` inner class.

```csharp
// Test: ServiceProvider_ResolvesIAutonomyTierResolver
//   Arrange: CreateBaseServices(), call AddInfrastructureAIDependencies(new AppConfig())
//   Act: provider.GetService<IAutonomyTierResolver>()
//   Assert: result.Should().NotBeNull().And.BeOfType<DefaultAutonomyTierResolver>()

// Test: ServiceProvider_ResolvesISupervisor
//   Arrange: CreateBaseServices(), call AddInfrastructureAIDependencies(new AppConfig())
//   Act: provider.GetService<ISupervisor>()
//   Assert: result.Should().NotBeNull().And.BeOfType<CapabilityMatchSupervisor>()

// Test: ServiceProvider_ResolvesIDelegationStore
//   Arrange: CreateBaseServices(), call AddInfrastructureAIDependencies(new AppConfig())
//   Act: provider.GetService<IDelegationStore>()
//   Assert: result.Should().NotBeNull().And.BeOfType<JsonlDelegationStore>()

// Test: ServiceProvider_ResolvesISupervisorStrategy_ByKey_CapabilityMatch
//   Arrange: CreateBaseServices(), call AddInfrastructureAIDependencies(new AppConfig())
//   Act: provider.GetKeyedService<ISupervisorStrategy>("capability-match")
//   Assert: result.Should().NotBeNull().And.BeOfType<CapabilityMatchStrategy>()

// Test: ServiceProvider_ResolvesIPermissionRuleProvider_IncludesAutonomyTierProvider
//   Arrange: CreateBaseServices(), call AddInfrastructureAIDependencies(new AppConfig()),
//            also call AddApplicationCoreDependencies() to register the Application.Core providers
//   Act: provider.GetServices<IPermissionRuleProvider>()
//   Assert: result.Should().Contain(p => p is AutonomyTierRuleProvider)
//   Note: AutonomyTierRuleProvider is registered in Application.Core DI, not Infrastructure.AI.
//         This test verifies cross-layer composition works when both DI methods are called.
```

Additional config normalization test (can go in a new test class or the existing one):

```csharp
// Test: CapabilityMatchWeightsConfig_NormalizesOnConstruction
//   Arrange: Create CapabilityMatchWeightsConfig { ToolCoverage = 0.4, TypeAlignment = 0.3, TierHeadroom = 0.5 }
//            (sum = 1.2, not 1.0)
//   Act: Call the Normalized() method (or verify constructor normalization)
//   Assert: After normalization: ToolCoverage ~= 0.333, TypeAlignment ~= 0.25, TierHeadroom ~= 0.417
//           Sum should equal 1.0 (within floating point tolerance)
```

---

## Implementation

### 1. New Config POCO: `AutonomyTierPolicyConfig`

**File:** `src/Content/Domain/Domain.Common/Config/AI/Permissions/AutonomyTierPolicyConfig.cs`

```csharp
namespace Domain.Common.Config.AI.Permissions;

/// <summary>
/// Configuration for a single autonomy tier's permission policy.
/// Bound from <c>AppConfig:AI:Permissions:TierPolicies:{TierName}</c>.
/// </summary>
public class AutonomyTierPolicyConfig
{
    /// <summary>
    /// Default permission behavior for this tier. Valid values: "Allow", "Ask", "Deny".
    /// Restricted and Supervised default to "Ask"; Autonomous defaults to "Allow".
    /// </summary>
    public string DefaultBehavior { get; set; } = "Ask";

    /// <summary>
    /// Per-tool behavior overrides within this tier. Key is tool name, value is behavior
    /// ("Allow", "Ask", "Deny"). Enables specific tools for otherwise restricted agents.
    /// </summary>
    public Dictionary<string, string>? ToolOverrides { get; set; }
}
```

This follows the existing config POCO pattern used by `PermissionsConfig`, `GovernanceConfig`, etc. in the `Domain.Common.Config.AI` namespace. Mutable setters are required for `IOptionsMonitor<T>` binding.

### 2. New Config POCO: `CapabilityMatchWeightsConfig`

**File:** `src/Content/Domain/Domain.Common/Config/AI/Orchestration/CapabilityMatchWeightsConfig.cs`

```csharp
namespace Domain.Common.Config.AI.Orchestration;

/// <summary>
/// Configurable weights for the capability match scoring algorithm.
/// Bound from <c>AppConfig:AI:Orchestration:Subagent:CapabilityMatchWeights</c>.
/// </summary>
/// <remarks>
/// Weights are normalized at consumption time by <c>CapabilityMatchStrategy</c>.
/// If configured values don't sum to 1.0, each is divided by the total to prevent
/// scores exceeding 1.0.
/// </remarks>
public class CapabilityMatchWeightsConfig
{
    /// <summary>Weight for tool coverage factor (default 0.4). Primary signal.</summary>
    public double ToolCoverage { get; set; } = 0.4;

    /// <summary>Weight for SubagentType alignment with task category (default 0.3).</summary>
    public double TypeAlignment { get; set; } = 0.3;

    /// <summary>Weight for tier headroom above minimum required (default 0.3).</summary>
    public double TierHeadroom { get; set; } = 0.3;
}
```

### 3. Extend `PermissionsConfig`

**File:** `src/Content/Domain/Domain.Common/Config/AI/Permissions/PermissionsConfig.cs`

Add two new properties to the existing class:

```csharp
/// <summary>
/// Default autonomy level assigned to agents that don't specify one in their
/// SubagentDefinition. Valid values: "Restricted", "Supervised", "Autonomous".
/// </summary>
public string DefaultAutonomyLevel { get; set; } = "Supervised";

/// <summary>
/// Per-tier policy overrides keyed by autonomy level name.
/// Each entry defines the default behavior and tool overrides for that tier.
/// </summary>
public Dictionary<string, AutonomyTierPolicyConfig> TierPolicies { get; set; } = new();
```

These sit alongside the existing `DefaultBehavior`, `DenialRateLimitThreshold`, `SafetyGatePaths`, and `MaxSubcommandLimit` properties.

### 4. Extend `SubagentConfig`

**File:** `src/Content/Domain/Domain.Common/Config/AI/Orchestration/SubagentConfig.cs`

Add new properties to the existing class:

```csharp
/// <summary>
/// Maximum depth of nested delegations (supervisor -> agent -> sub-agent).
/// Prevents infinite delegation chains. Default: 3.
/// </summary>
public int MaxDelegationDepth { get; set; } = 3;

/// <summary>
/// Filesystem path for delegation record storage (append-only JSONL).
/// Relative paths resolve from the working directory.
/// </summary>
public string DelegationStoragePath { get; set; } = ".agent-sessions/delegations";

/// <summary>
/// Maximum time in seconds to wait for a delegated agent to complete.
/// Expired delegations are cancelled and recorded as failed.
/// </summary>
public int DelegationTimeoutSeconds { get; set; } = 300;

/// <summary>
/// Maximum number of concurrent active delegations across all supervisors.
/// Additional requests block until a slot is available.
/// </summary>
public int MaxConcurrentDelegations { get; set; } = 5;

/// <summary>
/// Weights for the capability match scoring algorithm used by the supervisor
/// to select the best agent for a delegated task.
/// </summary>
public CapabilityMatchWeightsConfig CapabilityMatchWeights { get; set; } = new();
```

### 5. Extend `PermissionRuleSource` Enum

**File:** `src/Content/Domain/Domain.AI/Permissions/PermissionRuleSource.cs`

Add a new value:

```csharp
/// <summary>Rule generated from the agent's autonomy tier policy.</summary>
AutonomyTier
```

This enables audit log filtering to distinguish tier-generated rules from other policy sources (e.g., `PolicySettings` from AGT). Place it after `CliArgument` in the enum.

### 6. OTel Metric Conventions

**File:** `src/Content/Domain/Domain.AI/Telemetry/Conventions/SupervisorConventions.cs` (new file)

Create a new conventions class for supervisor/delegation metrics, following the pattern established by `GovernanceConventions` and `OrchestrationConventions`:

```csharp
namespace Domain.AI.Telemetry.Conventions;

/// <summary>Supervisor and delegation telemetry attribute names and metric identifiers.</summary>
public static class SupervisorConventions
{
    // Attribute names
    public const string SupervisorId = "agent.supervisor.id";
    public const string DelegateAgentId = "agent.supervisor.delegate_agent_id";
    public const string Outcome = "agent.supervisor.outcome";
    public const string AttemptedAction = "agent.supervisor.attempted_action";
    public const string CurrentTier = "agent.supervisor.current_tier";
    public const string AgentId = "agent.supervisor.agent_id";

    // Metric names
    public const string DelegationsTotal = "agent.supervisor.delegations.total";
    public const string DelegationDuration = "agent.supervisor.delegations.duration_ms";
    public const string AutonomyExceededTotal = "agent.supervisor.autonomy.exceeded_total";
    public const string SelectionScore = "agent.supervisor.selection_score";
}
```

### 7. OTel Metric Instruments

**File:** `src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/SupervisorMetrics.cs` (new file)

Create a new static metrics class following the `GovernanceMetrics` pattern:

```csharp
using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for supervisor delegation tracking,
/// autonomy tier violations, and capability match scoring.
/// </summary>
public static class SupervisorMetrics
{
    /// <summary>Total delegations. Tags: supervisor_id, delegate_agent_id, outcome.</summary>
    public static Counter<long> DelegationsTotal { get; } =
        AppInstrument.Meter.CreateCounter<long>(
            SupervisorConventions.DelegationsTotal, "{delegation}", "Total supervisor delegations");

    /// <summary>Delegation execution duration in milliseconds.</summary>
    public static Histogram<double> DelegationDuration { get; } =
        AppInstrument.Meter.CreateHistogram<double>(
            SupervisorConventions.DelegationDuration, "ms", "Delegation execution duration");

    /// <summary>Autonomy tier exceeded events. Tags: agent_id, attempted_action, current_tier.</summary>
    public static Counter<long> AutonomyExceededTotal { get; } =
        AppInstrument.Meter.CreateCounter<long>(
            SupervisorConventions.AutonomyExceededTotal, "{exceeded}", "Autonomy tier exceeded events");

    /// <summary>Capability match selection scores for observability.</summary>
    public static Histogram<double> SelectionScore { get; } =
        AppInstrument.Meter.CreateHistogram<double>(
            SupervisorConventions.SelectionScore, "{score}", "Capability match selection scores");
}
```

These instruments are consumed by `CapabilityMatchSupervisor` (section 07) to emit metrics during delegation. The static pattern matches `GovernanceMetrics` -- instruments are created once from `AppInstrument.Meter`.

### 8. Infrastructure.AI DI Registration

**File:** `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs`

Add the following registrations inside `AddInfrastructureAIDependencies`, after the existing subagent orchestration block (around line 210, after the `ISubagentProfileRegistry` registration):

```csharp
// Autonomy tier resolution — reads tier from SubagentDefinition or falls back to config
services.AddSingleton<IAutonomyTierResolver, DefaultAutonomyTierResolver>();

// Delegation persistence — append-only JSONL per supervisor session
services.AddSingleton<IDelegationStore, JsonlDelegationStore>();

// Supervisor strategy — deterministic capability-based agent selection, keyed for extensibility
services.AddKeyedSingleton<ISupervisorStrategy>("capability-match", (sp, _) =>
    new CapabilityMatchStrategy(sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));

// Supervisor — coordinates delegation, concurrency, depth tracking, and audit
services.AddSingleton<ISupervisor, CapabilityMatchSupervisor>();
```

Required `using` additions at the top of the file:
- `using Application.AI.Common.Interfaces.Governance;` (for `IAutonomyTierResolver`)
- `using Application.AI.Common.Interfaces.Agents;` (already present -- for `ISupervisor`, `ISupervisorStrategy`, `IDelegationStore`)

The `ISupervisorStrategy` uses keyed DI (`"capability-match"`) following the same pattern as tools (`FileSystemTool.ToolName`). This allows future strategies (e.g., `"llm-based"`, `"round-robin"`) to be registered alongside without changing the supervisor. The supervisor resolves the strategy by key from DI.

### 9. Application.Core DI Registration

**File:** `src/Content/Application/Application.Core/DependencyInjection.cs`

Add the following registration inside `AddApplicationCoreDependencies`, after the `AddValidatorsFromAssembly` call:

```csharp
// Autonomy tier rule provider — generates baseline permission rules from agent tier
services.AddSingleton<IPermissionRuleProvider, AutonomyTierRuleProvider>();
```

This is the only addition needed. The existing `ThreePhasePermissionResolver` already aggregates all `IPermissionRuleProvider` instances via `IEnumerable<IPermissionRuleProvider>`. Adding another provider here automatically includes it in rule aggregation.

Required `using` addition:
- `using Application.AI.Common.Interfaces.Permissions;` (for `IPermissionRuleProvider`)
- `using Application.Core.Permissions;` (for `AutonomyTierRuleProvider`)

Note: `AutonomyTierRuleProvider` lives in `Application.Core/Permissions/` (created in section 04), which is in the same assembly. The `ConfigBasedRuleProvider` lives in `Infrastructure.AI/Permissions/` and is registered there. This cross-layer registration is by design -- the tier rule provider is an Application-layer service (depends only on Application interfaces and Domain types), while `ConfigBasedRuleProvider` is an Infrastructure service (reads from filesystem/config).

### 10. Example `appsettings.json` Configuration

Update `src/Content/Presentation/Presentation.ConsoleUI/appsettings.json` to include the new configuration sections under the existing `AI` block. Add the `Permissions` and `Orchestration.Subagent` sections:

```json
{
  "AppConfig": {
    "AI": {
      "Permissions": {
        "DefaultBehavior": "Ask",
        "DefaultAutonomyLevel": "Supervised",
        "TierPolicies": {
          "Restricted": {
            "DefaultBehavior": "Ask",
            "ToolOverrides": {
              "query_knowledge_graph": "Allow"
            }
          },
          "Supervised": {
            "DefaultBehavior": "Ask",
            "ToolOverrides": {
              "query_knowledge_graph": "Allow",
              "file_system_read": "Allow"
            }
          },
          "Autonomous": {
            "DefaultBehavior": "Allow"
          }
        }
      },
      "Orchestration": {
        "Subagent": {
          "MaxConcurrentSubagents": 3,
          "DefaultMaxTurnsPerSubagent": 10,
          "MailboxStoragePath": ".agent-sessions/mailbox",
          "MaxDelegationDepth": 3,
          "DelegationStoragePath": ".agent-sessions/delegations",
          "DelegationTimeoutSeconds": 300,
          "MaxConcurrentDelegations": 5,
          "CapabilityMatchWeights": {
            "ToolCoverage": 0.4,
            "TypeAlignment": 0.3,
            "TierHeadroom": 0.3
          }
        }
      }
    }
  }
}
```

The config binding path is `AppConfig:AI:Permissions` for tier policies and `AppConfig:AI:Orchestration:Subagent` for delegation settings. This matches the existing hierarchy: `AppConfig.AI.Permissions` maps to `PermissionsConfig` and `AppConfig.AI.Orchestration.Subagent` maps to `SubagentConfig`. No new binding code is needed -- the existing `services.Configure<AppConfig>(configuration.GetSection("AppConfig"))` in the Presentation composition root handles recursive binding.

---

## Files Summary

| Action | File Path |
|--------|-----------|
| Create | `src/Content/Domain/Domain.Common/Config/AI/Permissions/AutonomyTierPolicyConfig.cs` |
| Create | `src/Content/Domain/Domain.Common/Config/AI/Orchestration/CapabilityMatchWeightsConfig.cs` |
| Modify | `src/Content/Domain/Domain.Common/Config/AI/Permissions/PermissionsConfig.cs` |
| Modify | `src/Content/Domain/Domain.Common/Config/AI/Orchestration/SubagentConfig.cs` |
| Modify | `src/Content/Domain/Domain.AI/Permissions/PermissionRuleSource.cs` |
| Create | `src/Content/Domain/Domain.AI/Telemetry/Conventions/SupervisorConventions.cs` |
| Create | `src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/SupervisorMetrics.cs` |
| Modify | `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs` |
| Modify | `src/Content/Application/Application.Core/DependencyInjection.cs` |
| Modify | `src/Content/Presentation/Presentation.ConsoleUI/appsettings.json` |
| Modify | `src/Content/Tests/Infrastructure.AI.Tests/DependencyInjectionTests.cs` |

---

## Key Design Decisions

1. **`AutonomyTierRuleProvider` registered in Application.Core, not Infrastructure.AI.** It only depends on Application interfaces (`IAutonomyTierResolver`, `IPermissionRuleProvider`) and Domain types. The litmus test passes: it compiles with only `Microsoft.Extensions.*` and Domain references. The existing `ConfigBasedRuleProvider` is in Infrastructure because it accesses filesystem config.

2. **`ISupervisorStrategy` uses keyed DI with key `"capability-match"`.** This follows the tool registration pattern (`AddKeyedSingleton<ITool>(ToolName, ...)`). The supervisor resolves the strategy by key, enabling multiple strategies to coexist. Future strategies register with different keys.

3. **Config lives in Domain.Common, not Application or Infrastructure.** Config POCOs are pure data containers with no framework dependencies beyond `System.Collections.Generic`. They follow the existing convention: every config class in this project lives under `Domain.Common/Config/`.

4. **`CapabilityMatchWeightsConfig` normalization happens at consumption time, not construction.** Config POCOs must have parameterless constructors and mutable setters for `IOptionsMonitor<T>` binding. The `CapabilityMatchStrategy` (section 05) normalizes weights when it reads them, not when the config is constructed. The test validates that consumption-time normalization produces correct results.

5. **Separate `SupervisorMetrics` class rather than extending `GovernanceMetrics`.** Governance and supervision are distinct concerns. Governance tracks policy decisions; supervision tracks delegation lifecycle. Separate metrics classes keep each focused and follow the existing single-responsibility pattern (`GovernanceMetrics` has 11 instruments already).

6. **OTel conventions in a separate `SupervisorConventions` class.** Follows the established pattern: each telemetry domain gets its own conventions class (`GovernanceConventions`, `OrchestrationConventions`, `PermissionConventions`). The metric name prefix `agent.supervisor.*` is consistent with the existing `agent.governance.*` and `agent.orchestration.*` namespaces.

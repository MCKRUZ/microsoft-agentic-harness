# Plugin-Boundary Governance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add enterprise governance controls to plugin declarations — AllowedTools/DeniedTools filtering at provisioning time and AutonomyLevel enforcement via the existing 3-phase permission resolver.

**Architecture:** Extends `PluginDeclaration` (config POCO) with three governance fields. Provisioning-time enforcement filters tools in `AgentExecutionContextFactory.BuildToolsAsync`. Execution-time enforcement emits `ToolPermissionRule` entries via a new `PluginPermissionRuleProvider` into the existing `ThreePhasePermissionResolver`. All changes are additive — plugins without governance config behave identically to today.

**Tech Stack:** C# .NET 10, xUnit, FluentAssertions, Moq, Microsoft.Extensions.AI

---

### Task 1: Add governance properties to PluginDeclaration and PermissionRuleSource enum

**Files:**
- Modify: `src/Content/Domain/Domain.Common/Config/AI/Plugins/PluginDeclaration.cs`
- Modify: `src/Content/Domain/Domain.AI/Permissions/PermissionRuleSource.cs`
- Test: `src/Content/Tests/Domain.AI.Tests/Permissions/PermissionRuleSourceTests.cs`

- [ ] **Step 1: Write the failing test for new enum value**

In `src/Content/Tests/Domain.AI.Tests/Permissions/PermissionRuleSourceTests.cs`, add a test verifying the new `PluginDeclaration` enum value exists. There should already be a test file here — if so, add to it. If not, create:

```csharp
using Domain.AI.Permissions;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Permissions;

public sealed class PermissionRuleSourceTests
{
    [Fact]
    public void PluginDeclaration_EnumValue_Exists()
    {
        var source = PermissionRuleSource.PluginDeclaration;

        source.Should().BeDefined();
        source.ToString().Should().Be("PluginDeclaration");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Content/Tests/Domain.AI.Tests/Domain.AI.Tests.csproj --filter "FullyQualifiedName~PermissionRuleSourceTests" -v m`
Expected: FAIL — `PluginDeclaration` not defined on enum.

- [ ] **Step 3: Add PluginDeclaration to PermissionRuleSource enum**

In `src/Content/Domain/Domain.AI/Permissions/PermissionRuleSource.cs`, add after the `AutonomyTier` value:

```csharp
    /// <summary>Rule generated from the agent's autonomy tier assignment.</summary>
    AutonomyTier,
    /// <summary>Rule from a plugin declaration's governance configuration.</summary>
    PluginDeclaration
```

Note: remove the trailing comma from `AutonomyTier` line only if it doesn't already have one (C# allows trailing commas in enums, but keep consistent with the file's style).

- [ ] **Step 4: Add governance properties to PluginDeclaration**

In `src/Content/Domain/Domain.Common/Config/AI/Plugins/PluginDeclaration.cs`, add three properties after `Env`. Add `using Domain.AI.Governance;` to the usings:

```csharp
using Domain.AI.Governance;

namespace Domain.Common.Config.AI.Plugins;

/// <summary>
/// A single plugin the harness should load from a local directory.
/// </summary>
public class PluginDeclaration
{
    /// <summary>Plugin identifier (e.g., "azure", "my-custom-tools").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Local filesystem path to the plugin directory containing plugin.json.
    /// Absolute or relative to the application's working directory.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Whether this plugin is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Environment variable overrides for this plugin's MCP servers.
    /// Merged with the plugin's declared env vars (declaration wins on conflict).
    /// </summary>
    public Dictionary<string, string> Env { get; set; } = new();

    /// <summary>
    /// Tool name whitelist for Injected-mode skills. When non-null, only these tools
    /// are provisioned. Applied before <see cref="DeniedTools"/>.
    /// </summary>
    public IReadOnlyList<string>? AllowedTools { get; set; }

    /// <summary>
    /// Tool name blacklist. Matched tools are removed after AllowedTools filtering.
    /// DeniedTools wins when a tool appears in both lists.
    /// </summary>
    public IReadOnlyList<string>? DeniedTools { get; set; }

    /// <summary>
    /// Autonomy level override for all tools from this plugin. Emits permission
    /// rules into the 3-phase resolver via <c>PluginPermissionRuleProvider</c>.
    /// </summary>
    public AutonomyLevel? AutonomyLevel { get; set; }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test src/Content/Tests/Domain.AI.Tests/Domain.AI.Tests.csproj --filter "FullyQualifiedName~PermissionRuleSourceTests" -v m`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Content/Domain/Domain.Common/Config/AI/Plugins/PluginDeclaration.cs src/Content/Domain/Domain.AI/Permissions/PermissionRuleSource.cs src/Content/Tests/Domain.AI.Tests/Permissions/PermissionRuleSourceTests.cs
git commit -m "feat(governance): add AllowedTools, DeniedTools, AutonomyLevel to PluginDeclaration"
```

---

### Task 2: Add Declaration to LoadedPlugin record and update PluginLoader

**Files:**
- Modify: `src/Content/Application/Application.AI.Common/Interfaces/Plugins/IPluginLoader.cs:24-31`
- Modify: `src/Content/Infrastructure/Infrastructure.AI/Plugins/PluginLoader.cs:50-56,63-70`
- Test: `src/Content/Tests/Infrastructure.AI.Tests/Plugins/PluginLoaderTests.cs` (if exists)

- [ ] **Step 1: Add Declaration parameter to LoadedPlugin record**

In `src/Content/Application/Application.AI.Common/Interfaces/Plugins/IPluginLoader.cs`, add `Declaration` as the last parameter. Add `using Domain.Common.Config.AI.Plugins;` if not already present (it already is for `PluginDeclaration` in the `Load` method):

```csharp
public record LoadedPlugin(
    string Name,
    string Version,
    string LocalPath,
    PluginManifest Manifest,
    PluginLoadStatus Status,
    IReadOnlyList<string> SkillPaths,
    IReadOnlyList<string> McpServerNames,
    PluginDeclaration Declaration);
```

- [ ] **Step 2: Fix the two constructor call sites in PluginLoader**

In `src/Content/Infrastructure/Infrastructure.AI/Plugins/PluginLoader.cs`, update both `new LoadedPlugin(...)` calls to pass `declaration` as the last argument.

Success path (~line 50):
```csharp
            return new LoadedPlugin(
                declaration.Name,
                manifest.Version,
                pluginPath,
                manifest,
                PluginLoadStatus.Loaded,
                skillPaths,
                mcpServerNames,
                declaration);
```

Failure path (~line 63):
```csharp
            return new LoadedPlugin(
                declaration.Name,
                manifest.Version,
                pluginPath,
                manifest,
                PluginLoadStatus.Failed,
                [],
                [],
                declaration);
```

- [ ] **Step 3: Build to verify no compilation errors**

Run: `dotnet build src/AgenticHarness.slnx`
Expected: Build succeeded. There may be other test files constructing `LoadedPlugin` that need updating — fix any that fail.

- [ ] **Step 4: Run all tests to check nothing broke**

Run: `dotnet test src/AgenticHarness.slnx -v m`
Expected: All tests pass. If any `LoadedPlugin` constructor calls in tests fail, update them to pass a `new PluginDeclaration()` as the last argument.

- [ ] **Step 5: Commit**

```bash
git add src/Content/Application/Application.AI.Common/Interfaces/Plugins/IPluginLoader.cs src/Content/Infrastructure/Infrastructure.AI/Plugins/PluginLoader.cs
git commit -m "feat(governance): add Declaration to LoadedPlugin record"
```

Note: also `git add` any test files you had to fix.

---

### Task 3: Create PluginPermissionRuleProvider

**Files:**
- Create: `src/Content/Application/Application.Core/Permissions/PluginPermissionRuleProvider.cs`
- Create: `src/Content/Tests/Application.Core.Tests/Permissions/PluginPermissionRuleProviderTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/Content/Tests/Application.Core.Tests/Permissions/PluginPermissionRuleProviderTests.cs`:

```csharp
using Application.AI.Common.Interfaces.Plugins;
using Application.Core.Permissions;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Moq;
using Xunit;

namespace Application.Core.Tests.Permissions;

public sealed class PluginPermissionRuleProviderTests
{
    private readonly Mock<IPluginRegistry> _registryMock = new();

    private PluginPermissionRuleProvider CreateProvider()
    {
        return new PluginPermissionRuleProvider(_registryMock.Object);
    }

    [Fact]
    public void Source_ReturnsPluginDeclaration()
    {
        var provider = CreateProvider();

        provider.Source.Should().Be(PermissionRuleSource.PluginDeclaration);
    }

    [Fact]
    public async Task GetRulesAsync_NoPluginsLoaded_ReturnsEmpty()
    {
        _registryMock
            .Setup(r => r.GetLoadedPlugins())
            .Returns(new List<LoadedPlugin>());

        var provider = CreateProvider();

        var rules = await provider.GetRulesAsync("any-agent");

        rules.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRulesAsync_PluginWithNoAutonomyLevel_ReturnsEmpty()
    {
        var declaration = new PluginDeclaration
        {
            Name = "azure",
            AutonomyLevel = null
        };
        _registryMock
            .Setup(r => r.GetLoadedPlugins())
            .Returns(new List<LoadedPlugin>
            {
                new("azure", "1.0", "/plugins/azure", new PluginManifest(),
                    PluginLoadStatus.Loaded, ["skill1"], ["azure:server"], declaration)
            });

        var provider = CreateProvider();

        var rules = await provider.GetRulesAsync("any-agent");

        rules.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRulesAsync_RestrictedPlugin_EmitsAskRulesForMcpTools()
    {
        var declaration = new PluginDeclaration
        {
            Name = "untrusted",
            AutonomyLevel = AutonomyLevel.Restricted
        };
        _registryMock
            .Setup(r => r.GetLoadedPlugins())
            .Returns(new List<LoadedPlugin>
            {
                new("untrusted", "1.0", "/plugins/untrusted", new PluginManifest(),
                    PluginLoadStatus.Loaded, [], ["untrusted:server"], declaration)
            });

        var provider = CreateProvider();

        var rules = await provider.GetRulesAsync("any-agent");

        rules.Should().ContainSingle();
        var rule = rules[0];
        rule.ToolPattern.Should().Be("untrusted:*");
        rule.Behavior.Should().Be(PermissionBehaviorType.Ask);
        rule.Source.Should().Be(PermissionRuleSource.PluginDeclaration);
    }

    [Fact]
    public async Task GetRulesAsync_AutonomousPlugin_EmitsAllowRules()
    {
        var declaration = new PluginDeclaration
        {
            Name = "trusted",
            AutonomyLevel = AutonomyLevel.Autonomous
        };
        _registryMock
            .Setup(r => r.GetLoadedPlugins())
            .Returns(new List<LoadedPlugin>
            {
                new("trusted", "1.0", "/plugins/trusted", new PluginManifest(),
                    PluginLoadStatus.Loaded, [], ["trusted:server"], declaration)
            });

        var provider = CreateProvider();

        var rules = await provider.GetRulesAsync("any-agent");

        rules.Should().ContainSingle();
        rules[0].Behavior.Should().Be(PermissionBehaviorType.Allow);
    }

    [Fact]
    public async Task GetRulesAsync_PluginWithDeniedTools_EmitsDenyRules()
    {
        var declaration = new PluginDeclaration
        {
            Name = "limited",
            AutonomyLevel = AutonomyLevel.Supervised,
            DeniedTools = ["bash", "deploy_production"]
        };
        _registryMock
            .Setup(r => r.GetLoadedPlugins())
            .Returns(new List<LoadedPlugin>
            {
                new("limited", "1.0", "/plugins/limited", new PluginManifest(),
                    PluginLoadStatus.Loaded, [], ["limited:server"], declaration)
            });

        var provider = CreateProvider();

        var rules = await provider.GetRulesAsync("any-agent");

        rules.Should().HaveCount(3); // 1 global Ask + 2 Deny overrides
        rules.Should().Contain(r => r.ToolPattern == "limited:*" && r.Behavior == PermissionBehaviorType.Ask);
        rules.Should().Contain(r => r.ToolPattern == "bash" && r.Behavior == PermissionBehaviorType.Deny);
        rules.Should().Contain(r => r.ToolPattern == "deploy_production" && r.Behavior == PermissionBehaviorType.Deny);
    }

    [Fact]
    public async Task GetRulesAsync_DenyRules_HaveHigherPriorityThanBaseline()
    {
        var declaration = new PluginDeclaration
        {
            Name = "plugin",
            AutonomyLevel = AutonomyLevel.Autonomous,
            DeniedTools = ["dangerous_tool"]
        };
        _registryMock
            .Setup(r => r.GetLoadedPlugins())
            .Returns(new List<LoadedPlugin>
            {
                new("plugin", "1.0", "/plugins/plugin", new PluginManifest(),
                    PluginLoadStatus.Loaded, [], ["plugin:server"], declaration)
            });

        var provider = CreateProvider();

        var rules = await provider.GetRulesAsync("any-agent");

        var baseline = rules.First(r => r.ToolPattern == "plugin:*");
        var deny = rules.First(r => r.ToolPattern == "dangerous_tool");

        deny.Priority.Should().BeLessThan(baseline.Priority);
        deny.Behavior.Should().Be(PermissionBehaviorType.Deny);
        deny.IsBypassImmune.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Content/Tests/Application.Core.Tests/Application.Core.Tests.csproj --filter "FullyQualifiedName~PluginPermissionRuleProviderTests" -v m`
Expected: FAIL — `PluginPermissionRuleProvider` class does not exist.

- [ ] **Step 3: Implement PluginPermissionRuleProvider**

Create `src/Content/Application/Application.Core/Permissions/PluginPermissionRuleProvider.cs`:

```csharp
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Plugins;
using Domain.AI.Governance;
using Domain.AI.Permissions;

namespace Application.Core.Permissions;

/// <summary>
/// Emits <see cref="ToolPermissionRule"/> entries from plugin declarations that specify
/// an <see cref="AutonomyLevel"/> override. Rules feed into the existing 3-phase
/// permission resolver alongside agent-level autonomy tier rules.
/// </summary>
public sealed class PluginPermissionRuleProvider : IPermissionRuleProvider
{
    private readonly IPluginRegistry _registry;

    public PluginPermissionRuleProvider(IPluginRegistry registry)
    {
        _registry = registry;
    }

    public PermissionRuleSource Source => PermissionRuleSource.PluginDeclaration;

    public Task<IReadOnlyList<ToolPermissionRule>> GetRulesAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var rules = new List<ToolPermissionRule>();

        foreach (var plugin in _registry.GetLoadedPlugins())
        {
            if (plugin.Declaration.AutonomyLevel is not { } autonomyLevel)
                continue;

            var defaultBehavior = autonomyLevel switch
            {
                AutonomyLevel.Autonomous => PermissionBehaviorType.Allow,
                _ => PermissionBehaviorType.Ask
            };

            // Baseline rule for all tools from this plugin (namespaced pattern)
            rules.Add(new ToolPermissionRule(
                $"{plugin.Name}:*",
                null,
                defaultBehavior,
                PermissionRuleSource.PluginDeclaration,
                Priority: 5));

            // Explicit deny rules for DeniedTools — higher priority, bypass-immune
            if (plugin.Declaration.DeniedTools is { Count: > 0 })
            {
                foreach (var denied in plugin.Declaration.DeniedTools)
                {
                    rules.Add(new ToolPermissionRule(
                        denied,
                        null,
                        PermissionBehaviorType.Deny,
                        PermissionRuleSource.PluginDeclaration,
                        Priority: 1,
                        IsBypassImmune: true));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<ToolPermissionRule>>(rules);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Content/Tests/Application.Core.Tests/Application.Core.Tests.csproj --filter "FullyQualifiedName~PluginPermissionRuleProviderTests" -v m`
Expected: All 7 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Content/Application/Application.Core/Permissions/PluginPermissionRuleProvider.cs src/Content/Tests/Application.Core.Tests/Permissions/PluginPermissionRuleProviderTests.cs
git commit -m "feat(governance): add PluginPermissionRuleProvider for execution-time enforcement"
```

---

### Task 4: Add provisioning-time tool boundary filtering in AgentExecutionContextFactory

**Files:**
- Modify: `src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs:1-60,373-395`
- Create: `src/Content/Tests/Application.AI.Common.Tests/Factories/AgentExecutionContextFactoryGovernanceTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/Content/Tests/Application.AI.Common.Tests/Factories/AgentExecutionContextFactoryGovernanceTests.cs`:

```csharp
using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Plugins;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

public sealed class AgentExecutionContextFactoryGovernanceTests
{
    private readonly Mock<IMcpToolProvider> _mcpToolProvider = new();
    private readonly Mock<IPluginRegistry> _pluginRegistry = new();

    private AgentExecutionContextFactory CreateFactory()
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    DefaultDeployment = "gpt-4o",
                    ClientType = AIAgentFrameworkClientType.AzureOpenAI
                }
            }
        };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);

        var services = new ServiceCollection();
        services.AddSingleton(_pluginRegistry.Object);

        return new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            services.BuildServiceProvider(),
            NullLoggerFactory.Instance,
            mcpToolProvider: _mcpToolProvider.Object);
    }

    private void SetupMcpTools(params (string server, string[] tools)[] servers)
    {
        var dict = new Dictionary<string, IList<AITool>>();
        foreach (var (server, tools) in servers)
        {
            dict[server] = tools
                .Select(t => (AITool)AIFunctionFactory.Create(() => "r", t))
                .ToList();
        }
        _mcpToolProvider
            .Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(dict);
    }

    private void SetupPlugin(string name, PluginDeclaration declaration)
    {
        var plugin = new LoadedPlugin(
            name, "1.0", $"/plugins/{name}", new PluginManifest(),
            PluginLoadStatus.Loaded, [], [$"{name}:server"], declaration);

        _pluginRegistry
            .Setup(r => r.GetPlugin(name))
            .Returns(plugin);
    }

    [Fact]
    public async Task Injected_AllowedTools_FiltersToWhitelist()
    {
        SetupMcpTools(("azure:server", ["az_cli", "bash", "deploy", "read_file"]));
        SetupPlugin("azure", new PluginDeclaration
        {
            Name = "azure",
            AllowedTools = ["az_cli", "read_file"]
        });

        var skill = new SkillDefinition
        {
            Id = "azure-skill", Name = "azure-skill",
            Instructions = "Deploy", PluginSource = "azure"
        };

        var context = await CreateFactory().MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Tools.Select(t => t.Name).Should().BeEquivalentTo(["az_cli", "read_file"]);
    }

    [Fact]
    public async Task Injected_DeniedTools_RemovesBlacklisted()
    {
        SetupMcpTools(("plugin:server", ["tool_a", "tool_b", "dangerous"]));
        SetupPlugin("plugin", new PluginDeclaration
        {
            Name = "plugin",
            DeniedTools = ["dangerous"]
        });

        var skill = new SkillDefinition
        {
            Id = "plugin-skill", Name = "plugin-skill",
            Instructions = "Do stuff", PluginSource = "plugin"
        };

        var context = await CreateFactory().MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Tools.Select(t => t.Name).Should().BeEquivalentTo(["tool_a", "tool_b"]);
    }

    [Fact]
    public async Task Injected_DeniedWinsOverAllowed()
    {
        SetupMcpTools(("plugin:server", ["a", "b", "c"]));
        SetupPlugin("plugin", new PluginDeclaration
        {
            Name = "plugin",
            AllowedTools = ["a", "b"],
            DeniedTools = ["b"]
        });

        var skill = new SkillDefinition
        {
            Id = "conflict-skill", Name = "conflict-skill",
            Instructions = "Test", PluginSource = "plugin"
        };

        var context = await CreateFactory().MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Tools.Select(t => t.Name).Should().BeEquivalentTo(["a"]);
    }

    [Fact]
    public async Task Injected_NoGovernanceConfig_AllToolsPassThrough()
    {
        SetupMcpTools(("azure:server", ["tool_1", "tool_2"]));
        _pluginRegistry
            .Setup(r => r.GetPlugin("azure"))
            .Returns((LoadedPlugin?)null);

        var skill = new SkillDefinition
        {
            Id = "ungoverned", Name = "ungoverned",
            Instructions = "No governance", PluginSource = "azure"
        };

        var context = await CreateFactory().MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Tools.Should().HaveCount(2);
    }

    [Fact]
    public async Task Injected_AllowedToolsOnly_NoMatchingTools_ReturnsEmpty()
    {
        SetupMcpTools(("plugin:server", ["tool_a", "tool_b"]));
        SetupPlugin("plugin", new PluginDeclaration
        {
            Name = "plugin",
            AllowedTools = ["nonexistent"]
        });

        var skill = new SkillDefinition
        {
            Id = "strict", Name = "strict",
            Instructions = "Strict", PluginSource = "plugin"
        };

        var context = await CreateFactory().MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Tools.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Content/Tests/Application.AI.Common.Tests/Application.AI.Common.Tests.csproj --filter "FullyQualifiedName~GovernanceTests" -v m`
Expected: FAIL — factory doesn't accept `IPluginRegistry` or apply governance filtering yet.

- [ ] **Step 3: Add IPluginRegistry to factory constructor and implement filtering**

In `src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs`:

**Add to usings:**
```csharp
using Application.AI.Common.Interfaces.Plugins;
```

**Add field:**
```csharp
    private readonly IPluginRegistry? _pluginRegistry;
```

**Add constructor parameter** (optional, after `resilientChatClientProvider`):
```csharp
    IPluginRegistry? pluginRegistry = null)
```

**In constructor body:**
```csharp
    _pluginRegistry = pluginRegistry;
```

Note: if the factory is constructed via DI and `IPluginRegistry` is already registered (it was registered in a prior gap fix), this will resolve automatically. If constructed manually in tests without it, it defaults to null (backwards compatible).

**Alternative approach** — resolve from `IServiceProvider` instead of adding a constructor parameter (avoids breaking existing constructor calls):

```csharp
// In the BuildToolsAsync injected mode block, after fetching tools:
var pluginRegistry = _serviceProvider.GetService<IPluginRegistry>();
```

Choose whichever approach causes fewer constructor-call fixups. The `_serviceProvider.GetService` approach requires zero constructor changes.

**Add filtering logic** in `BuildToolsAsync`, inside the `if (skill.Mode == SkillMode.Injected ...)` block, after `tools.AddRange(serverTools)` loop and before the AdditionalTools/dedup block (~line 385):

```csharp
        // Apply plugin-boundary governance filtering
        var pluginRegistry = _serviceProvider.GetService<IPluginRegistry>();
        var loadedPlugin = pluginRegistry?.GetPlugin(skill.PluginSource!);
        if (loadedPlugin != null)
            tools = ApplyPluginToolBoundary(tools, loadedPlugin.Declaration);
```

**Add the filtering method** as a private static method:

```csharp
    private static List<AITool> ApplyPluginToolBoundary(List<AITool> tools, PluginDeclaration declaration)
    {
        if (declaration.AllowedTools is { Count: > 0 } allowed)
        {
            var allowSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
            tools = tools.Where(t => allowSet.Contains(t.Name)).ToList();
        }

        if (declaration.DeniedTools is { Count: > 0 } denied)
        {
            var denySet = new HashSet<string>(denied, StringComparer.OrdinalIgnoreCase);
            tools = tools.Where(t => !denySet.Contains(t.Name)).ToList();
        }

        return tools;
    }
```

**Add using for PluginDeclaration** if not already imported:
```csharp
using Domain.Common.Config.AI.Plugins;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Content/Tests/Application.AI.Common.Tests/Application.AI.Common.Tests.csproj --filter "FullyQualifiedName~GovernanceTests" -v m`
Expected: All 5 tests PASS.

- [ ] **Step 5: Run all existing dual-mode tests still pass**

Run: `dotnet test src/Content/Tests/Application.AI.Common.Tests/Application.AI.Common.Tests.csproj --filter "FullyQualifiedName~DualModeTests" -v m`
Expected: All 5 existing tests PASS (they don't set up a registry, so `GetService<IPluginRegistry>()` returns null → no filtering).

- [ ] **Step 6: Commit**

```bash
git add src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs src/Content/Tests/Application.AI.Common.Tests/Factories/AgentExecutionContextFactoryGovernanceTests.cs
git commit -m "feat(governance): add provisioning-time tool boundary filtering for plugins"
```

---

### Task 5: Register PluginPermissionRuleProvider in DI

**Files:**
- Modify: `src/Content/Application/Application.Core/DependencyInjection.cs:44-47`

- [ ] **Step 1: Add registration**

In `src/Content/Application/Application.Core/DependencyInjection.cs`, after the `AutonomyTierRuleProvider` registration (~line 46), add:

```csharp
		// Plugin-boundary rule provider — generates permission rules from plugin governance config
		services.AddSingleton<IPermissionRuleProvider, PluginPermissionRuleProvider>();
```

Add `using Application.Core.Permissions;` if not already present.

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/AgenticHarness.slnx`
Expected: Build succeeded.

- [ ] **Step 3: Run full test suite**

Run: `dotnet test src/AgenticHarness.slnx -v m`
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/Content/Application/Application.Core/DependencyInjection.cs
git commit -m "feat(governance): register PluginPermissionRuleProvider in DI"
```

---

### Task 6: Full build verification and integration check

**Files:** None (verification only)

- [ ] **Step 1: Clean build**

Run: `dotnet build src/AgenticHarness.slnx --no-incremental`
Expected: Build succeeded, 0 errors, 0 warnings (or only pre-existing warnings).

- [ ] **Step 2: Full test suite**

Run: `dotnet test src/AgenticHarness.slnx -v m`
Expected: All tests pass.

- [ ] **Step 3: Verify new test counts**

Run: `dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~PluginPermissionRuleProvider|FullyQualifiedName~GovernanceTests|FullyQualifiedName~PermissionRuleSourceTests" -v m`
Expected: ~13 new tests all passing (7 rule provider + 5 governance + 1 enum).

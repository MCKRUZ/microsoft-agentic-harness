using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Governance;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Wiring integration tests (audit item I2) proving plugin boundary governance is live on the
/// REAL composition root: a plugin declared under <c>AppConfig:AI:Plugins:Packages</c> is loaded
/// by the production <c>PluginStartupLoader</c>, its skill is discovered and attributed by the
/// production <c>SkillMetadataRegistry</c>, its <c>DeniedTools</c> filter the production
/// <c>IToolChainBuilder</c> output, and its bypass-immune Deny rules block invocation through
/// the production scoped <see cref="IToolInvocationGovernor"/> — the same 3-gate chokepoint
/// (governor → classification → progress) every agent tool call passes through.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here hand-constructs the object graph. Every service is resolved from the graph
/// that <c>GetServices()</c> builds for real hosts, so a regression that unregisters a rule
/// provider, changes the governor's lifetime, or breaks plugin→skill attribution fails these
/// tests even when each component's isolated unit tests stay green.
/// </para>
/// <para>
/// The only test-supplied step is publishing the scoped governor to
/// <see cref="ToolGovernanceAccessor"/> — the single line
/// <c>ExecuteAgentTurnCommandHandler</c> performs at the start of a governed turn (and clears
/// in its finally). The tests mirror that exact pattern.
/// </para>
/// </remarks>
public sealed class PluginGovernanceCompositionTests : IDisposable
{
    private const string PluginName = "sentinel";
    private const string PluginSkillId = "sentinel-skill";
    private const string HostSkillId = "host-skill";
    private const string DeniedToolName = "dangerous_tool";

    // A real tool the plugin's skill declares. The plugin's AutonomyLevel baseline is scoped to
    // this name (not a synthetic "{plugin}:*" wildcard), mirroring how a live plugin tool is named.
    private const string PluginToolName = "deploy_widget";

    private readonly string _tempRoot;
    private readonly string _builtInSkillsDir;
    private readonly string _pluginDir;

    public PluginGovernanceCompositionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "composition-gov-" + Guid.NewGuid().ToString("N"));
        _builtInSkillsDir = Path.Combine(_tempRoot, "skills");
        _pluginDir = Path.Combine(_tempRoot, "plugins", PluginName);

        // Built-in (non-plugin) skill so the Managed resolution path is exercised too.
        WriteSkill(Path.Combine(_builtInSkillsDir, "host"), $"""
            ---
            name: {HostSkillId}
            description: A built-in skill with no tool declarations.
            ---
            Host instructions.
            """);

        // Plugin package on disk: plugin.json manifest + one skill under ./skills/.
        Directory.CreateDirectory(_pluginDir);
        File.WriteAllText(Path.Combine(_pluginDir, "plugin.json"), $$"""
            { "name": "{{PluginName}}", "version": "1.0.0", "skills": "./skills/" }
            """);
        WriteSkill(Path.Combine(_pluginDir, "skills", "do-thing"), $"""
            ---
            name: {PluginSkillId}
            description: A plugin skill declaring one tool so the autonomy baseline can scope to it.
            allowed-tools: [{PluginToolName}]
            ---
            Plugin instructions.
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private static void WriteSkill(string dir, string skillMarkdown)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), skillMarkdown);
    }

    /// <summary>
    /// Production-shaped configuration: one declared plugin with a DeniedTools boundary and an
    /// autonomy-level override, tool-invocation enforcement on, and an Allow-by-default
    /// permission environment so that any block observed is attributable to plugin governance
    /// rather than the resolver's fallback.
    /// </summary>
    private Dictionary<string, string?> BaseSettings(string pluginAutonomyLevel = "Supervised") => new()
    {
        ["AppConfig:AI:Skills:BasePath"] = _builtInSkillsDir,
        ["AppConfig:AI:Plugins:Packages:0:Name"] = PluginName,
        ["AppConfig:AI:Plugins:Packages:0:Path"] = _pluginDir,
        ["AppConfig:AI:Plugins:Packages:0:DeniedTools:0"] = DeniedToolName,
        ["AppConfig:AI:Plugins:Packages:0:AutonomyLevel"] = pluginAutonomyLevel,
        ["AppConfig:AI:Governance:EnforceToolInvocation"] = "true",
        ["AppConfig:AI:Permissions:DefaultBehavior"] = "Allow",
    };

    [Fact]
    public async Task PluginStartupLoader_DeclaredPlugin_LoadsAndAttributesSkillThroughCompositionRoot()
    {
        await using var provider = CompositionRootTestHost.BuildProvider(BaseSettings());

        await CompositionRootTestHost.RunPluginStartupLoaderAsync(provider);

        var registry = provider.GetRequiredService<IPluginRegistry>();
        var plugin = registry.GetPlugin(PluginName);
        plugin.Should().NotBeNull("the declared plugin must be loaded by the registered hosted service");
        plugin!.Declaration.DeniedTools.Should().Contain(DeniedToolName);

        var skillRegistry = provider.GetRequiredService<ISkillMetadataRegistry>();
        var skill = skillRegistry.TryGet(PluginSkillId);
        skill.Should().NotBeNull("the plugin's skill directory must be visible to lazy skill discovery");
        skill!.PluginSource.Should().Be(PluginName,
            "boundary governance only fires when discovery attributes the skill to its plugin");
    }

    [Fact]
    public async Task BuildToolsAsync_PluginSkillWithDeniedTool_FiltersDeniedToolOnLivePath()
    {
        await using var provider = CompositionRootTestHost.BuildProvider(BaseSettings());
        await CompositionRootTestHost.RunPluginStartupLoaderAsync(provider);

        var skill = provider.GetRequiredService<ISkillMetadataRegistry>().TryGet(PluginSkillId);
        skill.Should().NotBeNull();

        var builder = provider.GetRequiredService<IToolChainBuilder>();
        var options = new Domain.AI.Skills.SkillAgentOptions
        {
            AdditionalTools =
            [
                AIFunctionFactory.Create(() => "ok", "audit_tool"),
                AIFunctionFactory.Create(() => "boom", DeniedToolName),
            ],
        };

        var tools = await builder.BuildToolsAsync(skill!, options);

        tools.Select(t => t.Name).Should().NotContain(DeniedToolName,
            "the plugin's DeniedTools boundary must remove the tool from the resolved set on the live path");
        tools.Select(t => t.Name).Should().Contain("audit_tool");
        tools.Should().OnlyContain(t => t.GetType().Name == "GovernedAIFunction",
            "every agent-callable tool must be wrapped in the per-invocation governance chokepoint");
    }

    [Fact]
    public async Task GovernedTool_PluginDeniedTool_BlockedAtInvocationByRealGovernor()
    {
        // A denied tool that leaks into the toolset via a NON-plugin skill (so the boundary
        // filter cannot remove it) must still be blocked at invocation time: the plugin's
        // bypass-immune Deny rule flows PluginPermissionRuleProvider → ThreePhasePermissionResolver
        // → ToolInvocationGovernor → GovernedAIFunction, all resolved from the production graph.
        await using var provider = CompositionRootTestHost.BuildProvider(BaseSettings());
        await CompositionRootTestHost.RunPluginStartupLoaderAsync(provider);

        var executed = false;
        var wrapped = await BuildGovernedHostTool(provider,
            AIFunctionFactory.Create(() => { executed = true; return "ran"; }, DeniedToolName));

        using var scope = provider.CreateScope();
        var (results, trace) = await InvokeUnderGovernedTurn(scope, wrapped);

        ResultText(results[0]).Should().Contain("is not permitted",
            "the governor's model-facing denial must replace the tool result");
        executed.Should().BeFalse("a governance-denied tool must never execute");
        trace.ToolDecisions.Should().ContainSingle(d =>
            d.ToolName == DeniedToolName && d.Outcome == ToolDecisionOutcome.Denied);
    }

    [Fact]
    public async Task GovernedTool_PluginSupervisedBaseline_TightensRealDeclaredToolToPendingApproval()
    {
        // A Supervised plugin baseline maps to an authoritative Ask scoped to the plugin's REAL
        // declared tool ("deploy_widget"), enumerated from the plugin skill's allowed-tools. The
        // authoritative baseline tightens the otherwise Allow-by-default environment for that tool,
        // while an unrelated tool stays allowed. (Pre-fix this used a synthetic "{plugin}:*"
        // wildcard that no live tool name ever matched, so the baseline was inert.)
        await using var provider = CompositionRootTestHost.BuildProvider(BaseSettings("Supervised"));
        await CompositionRootTestHost.RunPluginStartupLoaderAsync(provider);

        var pluginToolExecuted = false;
        var plainExecuted = false;
        var pluginTool = await BuildGovernedHostTool(provider,
            AIFunctionFactory.Create(() => { pluginToolExecuted = true; return "ran"; }, PluginToolName));
        var plain = await BuildGovernedHostTool(provider,
            AIFunctionFactory.Create(() => { plainExecuted = true; return "ran"; }, "plain_tool"));

        using var scope = provider.CreateScope();
        var (results, trace) = await InvokeUnderGovernedTurn(scope, pluginTool, plain);

        ResultText(results[0]).Should().Contain("is not permitted",
            "the plugin's Supervised baseline maps to Ask, which fail-closes to a block pending approval");
        pluginToolExecuted.Should().BeFalse();
        plainExecuted.Should().BeTrue("a tool outside the plugin's declared surface stays allowed");
        trace.ToolDecisions.Should().Contain(d =>
            d.ToolName == PluginToolName && d.Outcome == ToolDecisionOutcome.PendingApproval);
        trace.ToolDecisions.Should().Contain(d =>
            d.ToolName == "plain_tool" && d.Outcome == ToolDecisionOutcome.Allowed);
    }

    [Fact]
    public async Task GovernedTool_AutonomousPluginBaseline_LoosensRealDeclaredToolUnderStricterDefault()
    {
        // ENFORCED AUTONOMY BASELINE (was "known-inert" before the fix).
        //
        // A plugin declaring AutonomyLevel=Autonomous now emits an *authoritative* Allow baseline
        // scoped to its REAL declared tool ("deploy_widget"). The resolver's authoritative-baseline
        // phase runs before the Ask/Allow phases, so the plugin's Allow LOOSENS an otherwise
        // stricter (DefaultBehavior=Ask) environment for that tool — while an unrelated tool still
        // gets the strict tier Ask. This is the operator's per-plugin trust decision winning over
        // the generic default, without touching the resolver's fail-safe Deny precedence.
        var settings = BaseSettings("Autonomous");
        settings["AppConfig:AI:Permissions:DefaultBehavior"] = "Ask"; // stricter generic default
        await using var provider = CompositionRootTestHost.BuildProvider(settings);
        await CompositionRootTestHost.RunPluginStartupLoaderAsync(provider);

        var pluginToolExecuted = false;
        var plainExecuted = false;
        var pluginTool = await BuildGovernedHostTool(provider,
            AIFunctionFactory.Create(() => { pluginToolExecuted = true; return "ran"; }, PluginToolName));
        var plain = await BuildGovernedHostTool(provider,
            AIFunctionFactory.Create(() => { plainExecuted = true; return "ran"; }, "plain_tool"));

        using var scope = provider.CreateScope();
        var (results, trace) = await InvokeUnderGovernedTurn(scope, pluginTool, plain);

        pluginToolExecuted.Should().BeTrue(
            "the plugin's Autonomous authoritative baseline loosens its declared tool to Allow");
        ResultText(results[0]).Should().Be("ran");
        ResultText(results[1]).Should().Contain("is not permitted",
            "a tool outside the plugin's declared surface still hits the strict tier Ask default");
        plainExecuted.Should().BeFalse();
        trace.ToolDecisions.Should().Contain(d =>
            d.ToolName == PluginToolName && d.Outcome == ToolDecisionOutcome.Allowed);
        trace.ToolDecisions.Should().Contain(d =>
            d.ToolName == "plain_tool" && d.Outcome == ToolDecisionOutcome.PendingApproval);
    }

    /// <summary>
    /// Extracts the model-facing text from an <see cref="AIFunction.InvokeAsync"/> result.
    /// The invocation pipeline marshals return values through JSON, so a governance denial
    /// string arrives as a <see cref="System.Text.Json.JsonElement"/> rather than a raw string.
    /// </summary>
    private static string ResultText(object? invocationResult) => invocationResult switch
    {
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element
            => element.GetString()!,
        _ => invocationResult?.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Builds the governed tool set for the built-in (non-plugin) skill through the production
    /// <see cref="IToolChainBuilder"/>, returning the wrapped functions for the given probes.
    /// </summary>
    private static async Task<AIFunction> BuildGovernedHostTool(
        ServiceProvider provider, AIFunction probe)
    {
        var skill = provider.GetRequiredService<ISkillMetadataRegistry>().TryGet(HostSkillId);
        skill.Should().NotBeNull("the built-in skill must be discoverable from the configured BasePath");

        var tools = await provider.GetRequiredService<IToolChainBuilder>().BuildToolsAsync(
            skill!, new Domain.AI.Skills.SkillAgentOptions { AdditionalTools = [probe] });

        return tools.OfType<AIFunction>().Single(t => t.Name == probe.Name);
    }

    /// <summary>
    /// Invokes the given governed functions inside a governed turn shaped exactly like
    /// <c>ExecuteAgentTurnCommandHandler</c>'s: scoped execution context initialized with an
    /// agent identity, the scope's governor published to <see cref="ToolGovernanceAccessor"/>,
    /// and the accessor cleared in a finally. Returns each invocation's result (in call order)
    /// plus the governor's recorded trace.
    /// </summary>
    private static async Task<(IReadOnlyList<object?> Results, GovernanceTrace Trace)> InvokeUnderGovernedTurn(
        IServiceScope scope, params AIFunction[] functions)
    {
        scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>()
            .Initialize("composition-test-agent", "conv-gov", turnNumber: 1);

        var governor = scope.ServiceProvider.GetRequiredService<IToolInvocationGovernor>();
        ToolGovernanceAccessor.Current = governor;
        try
        {
            var results = new List<object?>();
            foreach (var function in functions)
                results.Add(await function.InvokeAsync(new AIFunctionArguments(), CancellationToken.None));

            return (results, governor.GetTrace());
        }
        finally
        {
            ToolGovernanceAccessor.Current = null;
        }
    }
}

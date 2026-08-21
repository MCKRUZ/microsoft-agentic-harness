using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.AI.Models;
using Domain.AI.Planner;
using Domain.AI.Skills;
using Domain.AI.Tools;
using FluentAssertions;
using Infrastructure.AI.Telemetry.Redaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// Tests the runtime half of the reserved plan-capability defence in <see cref="ToolChainBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ReservedPlanCapabilityGuard</c> scans DI descriptors at boot, which covers first-party keyed
/// tools only. MCP-client and plugin-manifest tools are discovered at <em>runtime</em>, so a
/// third-party server publishing <c>rag_retrieval</c> is invisible to any boot-time scan. Because
/// plan capabilities are authorized out of the same case-insensitively matched
/// <c>CapabilityEnvelope.AllowedTools</c> string space as tool names, such a tool would be handed to
/// the model by any plan envelope that grants retrieval — and an envelope granting the tool would
/// grant plan inference. These tests pin that a colliding runtime tool never reaches the callable
/// surface, that casing cannot be used to evade the check, and that non-colliding tools are untouched.
/// </para>
/// </remarks>
public sealed class ToolChainBuilderReservedCapabilityTests
{
    private static readonly IContentRedactionFilter RedactionFilter = TestRedactionFilter.Instance;

    private static ToolChainBuilder Builder(IMcpToolProvider mcp) => new(
        NullLogger<ToolChainBuilder>.Instance,
        new ServiceCollection().BuildServiceProvider(),
        RedactionFilter,
        toolConverter: null,
        mcpToolProvider: mcp);

    /// <summary>
    /// A plugin-sourced skill with no declarations or restrictions, which is what
    /// <see cref="SkillDefinition.Mode"/> derives as <see cref="SkillMode.Injected"/> — the path that
    /// passes every MCP tool through wholesale, and therefore the widest third-party inlet.
    /// </summary>
    private static SkillDefinition InjectedSkill() =>
        new() { Id = "s", Name = "s", Instructions = "x", PluginSource = "third-party-plugin" };

    private static IMcpToolProvider McpPublishing(params string[] toolNames)
    {
        var mcp = new Mock<IMcpToolProvider>();
        mcp.Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>>
            {
                ["third-party"] = [.. toolNames.Select(n => (AITool)AIFunctionFactory.Create(() => "r", n))]
            });
        return mcp.Object;
    }

    [Theory]
    [InlineData(PlanCapabilities.Retrieval)]
    [InlineData(PlanCapabilities.LlmCall)]
    [InlineData("RAG_Retrieval")]
    [InlineData("LLM_Call")]
    public async Task InjectedMcpTool_NamedForReservedPlanCapability_IsExcludedFromChain(string publishedName)
    {
        // An external MCP server publishes a tool under a reserved plan-capability name. It must never
        // reach the model's callable surface: an envelope granting the capability would otherwise also
        // hand over this third-party tool. Casing must not evade the check, because the allowlist that
        // authorizes the capability matches case-insensitively.
        var builder = Builder(McpPublishing(publishedName, "safe_tool"));

        var tools = await builder.BuildToolsAsync(InjectedSkill(), new SkillAgentOptions());

        tools.Select(t => t.Name).Should().BeEquivalentTo(["safe_tool"],
            "a runtime-sourced tool colliding with a reserved plan capability must be dropped, " +
            "while its non-colliding neighbours pass through");
    }

    [Fact]
    public async Task InjectedMcpTools_WithoutCollision_PassThroughUntouched()
    {
        var builder = Builder(McpPublishing("weather_lookup", "ticket_search"));

        var tools = await builder.BuildToolsAsync(InjectedSkill(), new SkillAgentOptions());

        tools.Select(t => t.Name).Should().BeEquivalentTo(["weather_lookup", "ticket_search"],
            "the filter must be inert for every name outside PlanCapabilities.ReservedNames");
    }

    [Fact]
    public async Task DeclarationResolvedMcpTool_NamedForReservedPlanCapability_IsExcludedFromChain()
    {
        // The managed resolution path is a separate entry point from Injected mode: a ToolDeclaration
        // satisfied MCP-first brings the third party's names in just the same.
        var mcp = new Mock<IMcpToolProvider>();
        mcp.Setup(p => p.GetToolsAsync("third-party", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                AIFunctionFactory.Create(() => "r", PlanCapabilities.Retrieval),
                AIFunctionFactory.Create(() => "r", "safe_tool")
            ]);

        var builder = Builder(mcp.Object);
        var skill = new SkillDefinition
        {
            Id = "s",
            Name = "s",
            Instructions = "x",
            ToolDeclarations = [new ToolDeclaration { Name = "third-party" }]
        };

        var tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        tools.Select(t => t.Name).Should().BeEquivalentTo(["safe_tool"]);
    }

    [Fact]
    public async Task MergedChain_UnderEnvelopeGrantingRetrieval_StillExcludesCollidingMcpTool()
    {
        // The reachability case in full: the envelope grants rag_retrieval because the plan must be able
        // to retrieve, and the same string is what the merged-chain allowlist matches on. Without the
        // filter the third-party tool would be admitted by that very grant.
        var builder = Builder(McpPublishing(PlanCapabilities.Retrieval, "safe_tool"));

        var merged = await builder.BuildMergedToolsWithSourcesAsync(
            [InjectedSkill()],
            new SkillAgentOptions(),
            allowedTools: [PlanCapabilities.Retrieval, "safe_tool"]);

        merged.Tools.Select(t => t.Name).Should().BeEquivalentTo(["safe_tool"],
            "granting a plan capability must never silently grant a same-named third-party tool");
        merged.McpToolNames.Should().NotContain(PlanCapabilities.Retrieval,
            "a dropped tool must not be attributed as a surviving MCP-sourced tool");
    }

    [Fact]
    public void BuildToolsByName_NamedForReservedPlanCapability_IsExcludedFromChain()
    {
        // Keyed DI should never legitimately hold such a key — ReservedPlanCapabilityGuard rejects it at
        // boot — so this deliberately registers one to prove the by-name entry point routes through the
        // same exit filter rather than around it. A consumer host that registers tools after the harness
        // composition root returns is exactly the case where the boot guard has already run.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>(PlanCapabilities.LlmCall, (_, _) => new StubTool(PlanCapabilities.LlmCall));
        services.AddKeyedSingleton<ITool>("safe_tool", (_, _) => new StubTool("safe_tool"));

        var builder = new ToolChainBuilder(
            NullLogger<ToolChainBuilder>.Instance,
            services.BuildServiceProvider(),
            RedactionFilter,
            new PassThroughToolConverter(),
            new Mock<IMcpToolProvider>().Object);

        var tools = builder.BuildToolsByName([PlanCapabilities.LlmCall, "safe_tool"]);

        tools.Select(t => t.Name).Should().BeEquivalentTo(["safe_tool"],
            "the by-name path must apply the reserved filter, not bypass it");
    }

}

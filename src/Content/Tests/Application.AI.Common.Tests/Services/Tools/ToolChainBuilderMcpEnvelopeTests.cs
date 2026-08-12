using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Tools;
using Domain.AI.Bundles;
using Domain.AI.Skills;
using Domain.AI.Tools;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// Tests the reference-only MCP allowlist in <see cref="ToolChainBuilder"/>: on a bundle run (an ambient
/// capability envelope) only MCP servers the envelope names are reachable, and — critically — an ungranted
/// server is never <em>contacted</em>, so a bundle cannot even probe a host server it was not granted
/// (no SSRF, no tool-schema disclosure). Off the bundle path (no envelope) every server passes through
/// unchanged, which the existing suite already covers.
/// </summary>
public sealed class ToolChainBuilderMcpEnvelopeTests
{
    private static ToolChainBuilder Builder(IMcpToolProvider mcp, IServiceProvider? sp = null) => new(
        NullLogger<ToolChainBuilder>.Instance,
        sp ?? new ServiceCollection().BuildServiceProvider(),
        toolConverter: null,
        mcpToolProvider: mcp);

    private static SkillDefinition InjectedSkill() =>
        new() { Id = "bundle-skill", Name = "bundle-skill", Instructions = "x", PluginSource = "bundle" };

    [Fact]
    public async Task InjectedMode_WithEnvelope_ContactsOnlyGrantedServers()
    {
        var mcp = new Mock<IMcpToolProvider>();
        mcp.Setup(p => p.GetToolsAsync("granted-server", It.IsAny<CancellationToken>()))
            .ReturnsAsync([AIFunctionFactory.Create(() => "r", "granted_tool")]);

        var builder = Builder(mcp.Object);

        List<AITool> tools;
        using (CapabilityEnvelopeAccessor.Begin(Envelope("granted-server")))
            tools = await builder.BuildToolsAsync(InjectedSkill(), new SkillAgentOptions());

        tools.Select(t => t.Name).Should().BeEquivalentTo(["granted_tool"]);
        // The forbidden server is never contacted, and the all-servers enumeration is never called.
        mcp.Verify(p => p.GetToolsAsync("granted-server", It.IsAny<CancellationToken>()), Times.Once);
        mcp.Verify(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()), Times.Never,
            "under an envelope we must never enumerate every host server — that would contact ungranted ones");
    }

    [Fact]
    public async Task InjectedMode_NamespacedGrant_PublishesNamespacedToolName()
    {
        // A bundle-owned server is reached via its namespaced key ("{bundleId}:{serverName}") even in
        // Injected mode, where every granted server's tools pass straight through. The tool's own name
        // must still be namespaced — never the bare, bundle-chosen name — for the same reason the Managed
        // path already namespaces it: the bare name is what CapabilityEnvelope.AllowedTools was granted
        // under (BundleRunExecutor), and it must never collide with an unrelated host tool of the same name.
        var mcp = new Mock<IMcpToolProvider>();
        mcp.Setup(p => p.GetToolsAsync("bundle-123:epr-mcp", It.IsAny<CancellationToken>()))
            .ReturnsAsync([AIFunctionFactory.Create(() => "r", "epr_tool")]);

        var builder = Builder(mcp.Object);

        List<AITool> tools;
        using (CapabilityEnvelopeAccessor.Begin(Envelope("bundle-123:epr-mcp")))
            tools = await builder.BuildToolsAsync(InjectedSkill(), new SkillAgentOptions());

        tools.Select(t => t.Name).Should().Contain("bundle-123_epr-mcp__epr_tool");
        tools.Select(t => t.Name).Should().NotContain("epr_tool",
            "the bare, bundle-chosen name must never be the model-callable/governed name in Injected mode either");
    }

    [Fact]
    public async Task InjectedMode_HostConfiguredGrant_PublishesBareToolName()
    {
        // A plain, host-configured server name (no colon) is not bundle-owned — its tools keep the bare
        // name exactly as before, so this fix must not change behaviour for the pre-existing, trusted path.
        var mcp = new Mock<IMcpToolProvider>();
        mcp.Setup(p => p.GetToolsAsync("granted-server", It.IsAny<CancellationToken>()))
            .ReturnsAsync([AIFunctionFactory.Create(() => "r", "granted_tool")]);

        var builder = Builder(mcp.Object);

        List<AITool> tools;
        using (CapabilityEnvelopeAccessor.Begin(Envelope("granted-server")))
            tools = await builder.BuildToolsAsync(InjectedSkill(), new SkillAgentOptions());

        tools.Select(t => t.Name).Should().BeEquivalentTo(["granted_tool"]);
    }

    [Fact]
    public async Task InjectedMode_NoEnvelope_EnumeratesAllServers()
    {
        var mcp = new Mock<IMcpToolProvider>();
        mcp.Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>>
            {
                ["server-a"] = [AIFunctionFactory.Create(() => "r", "tool_a")],
                ["server-b"] = [AIFunctionFactory.Create(() => "r", "tool_b")]
            });

        var builder = Builder(mcp.Object);

        var tools = await builder.BuildToolsAsync(InjectedSkill(), new SkillAgentOptions());

        tools.Select(t => t.Name).Should().BeEquivalentTo(["tool_a", "tool_b"],
            "off the bundle path there is no envelope and every server is reachable");
    }

    [Fact]
    public async Task InjectedMode_EmptyEnvelope_ContactsNoServer()
    {
        var mcp = new Mock<IMcpToolProvider>();
        var builder = Builder(mcp.Object);

        List<AITool> tools;
        using (CapabilityEnvelopeAccessor.Begin(Envelope(/* no servers granted */)))
            tools = await builder.BuildToolsAsync(InjectedSkill(), new SkillAgentOptions());

        tools.Should().BeEmpty("a fail-closed envelope grants no MCP server");
        mcp.Verify(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()), Times.Never);
        mcp.Verify(p => p.GetToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ManagedDeclaration_ForbiddenServer_NeverContacted_FallsToKeyedDi()
    {
        // The declaration names a server the envelope does not grant. The MCP attempt must be skipped
        // entirely so the forbidden server is never contacted; resolution falls through to keyed DI, which
        // here has nothing, so the optional tool resolves to empty.
        var mcp = new Mock<IMcpToolProvider>();
        mcp.Setup(p => p.GetToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([AIFunctionFactory.Create(() => "r", "forbidden_tool")]);

        var builder = Builder(mcp.Object);
        var skill = new SkillDefinition
        {
            Id = "s", Name = "s", Instructions = "x",
            ToolDeclarations = [new ToolDeclaration { Name = "forbidden-server", Optional = true }]
        };

        List<AITool> tools;
        using (CapabilityEnvelopeAccessor.Begin(Envelope("granted-server")))
            tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        tools.Should().BeEmpty();
        mcp.Verify(p => p.GetToolsAsync("forbidden-server", It.IsAny<CancellationToken>()), Times.Never,
            "a forbidden server must never even be contacted");
    }

    [Fact]
    public async Task ManagedDeclaration_GrantedServer_ResolvesFromMcp_CaseInsensitiveGrant()
    {
        // The envelope grants "Granted-Server" (different casing); the declaration names "granted-server".
        // The grant match is case-insensitive, so casing cannot be used to spoof or evade a grant.
        var mcp = new Mock<IMcpToolProvider>();
        mcp.Setup(p => p.GetToolsAsync("granted-server", It.IsAny<CancellationToken>()))
            .ReturnsAsync([AIFunctionFactory.Create(() => "r", "granted_tool")]);

        var builder = Builder(mcp.Object);
        var skill = new SkillDefinition
        {
            Id = "s", Name = "s", Instructions = "x",
            ToolDeclarations = [new ToolDeclaration { Name = "granted-server" }]
        };

        List<AITool> tools;
        using (CapabilityEnvelopeAccessor.Begin(Envelope("Granted-Server")))
            tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        tools.Select(t => t.Name).Should().Contain("granted_tool");
    }

    // -- Namespaced-grant suffix resolution (issue #368) --
    // A skill's declared server name is bundle-agnostic (e.g. "epr-mcp"); a bundle's own server is granted
    // under a namespaced key ("{bundleId}:epr-mcp") the author could not have known at authoring time.

    [Fact]
    public async Task ManagedDeclaration_NamespacedGrant_ResolvesViaSuffixMatch()
    {
        var mcp = new Mock<IMcpToolProvider>();
        mcp.Setup(p => p.GetToolsAsync("bundle-123:epr-mcp", It.IsAny<CancellationToken>()))
            .ReturnsAsync([AIFunctionFactory.Create(() => "r", "epr_tool")]);

        var builder = Builder(mcp.Object);
        var skill = new SkillDefinition
        {
            Id = "s", Name = "s", Instructions = "x",
            ToolDeclarations = [new ToolDeclaration { Name = "epr-mcp" }]
        };

        List<AITool> tools;
        using (CapabilityEnvelopeAccessor.Begin(Envelope("bundle-123:epr-mcp")))
            tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        // Published under a NAMESPACED name, never the bare name the (untrusted, bundle-authored)
        // server declared — see BundleOwnedMcpToolNaming for why a bare name would be exploitable.
        tools.Select(t => t.Name).Should().Contain("bundle-123_epr-mcp__epr_tool");
        tools.Select(t => t.Name).Should().NotContain("epr_tool",
            "the bare, bundle-chosen name must never be the model-callable/governed name");
        mcp.Verify(p => p.GetToolsAsync("bundle-123:epr-mcp", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ManagedDeclaration_ExactGrantWinsOverSuffixMatch()
    {
        // Both an exact grant AND a namespaced one ending in the same suffix are present. The exact grant
        // must win outright — a host-configured, non-namespaced server is never redirected by the fallback.
        var mcp = new Mock<IMcpToolProvider>();
        mcp.Setup(p => p.GetToolsAsync("epr-mcp", It.IsAny<CancellationToken>()))
            .ReturnsAsync([AIFunctionFactory.Create(() => "r", "host_tool")]);

        var builder = Builder(mcp.Object);
        var skill = new SkillDefinition
        {
            Id = "s", Name = "s", Instructions = "x",
            ToolDeclarations = [new ToolDeclaration { Name = "epr-mcp" }]
        };

        List<AITool> tools;
        using (CapabilityEnvelopeAccessor.Begin(Envelope("epr-mcp", "other-bundle:epr-mcp")))
            tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        tools.Select(t => t.Name).Should().Contain("host_tool");
        mcp.Verify(p => p.GetToolsAsync("epr-mcp", It.IsAny<CancellationToken>()), Times.Once);
        mcp.Verify(p => p.GetToolsAsync("other-bundle:epr-mcp", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ManagedDeclaration_AmbiguousSuffixMatch_IsDeniedNotGuessed()
    {
        // Two different namespaced grants end in the same declared name with no exact grant to break the
        // tie. Guessing either one would be arbitrary, so neither is contacted.
        var mcp = new Mock<IMcpToolProvider>();
        var builder = Builder(mcp.Object);
        var skill = new SkillDefinition
        {
            Id = "s", Name = "s", Instructions = "x",
            ToolDeclarations = [new ToolDeclaration { Name = "epr-mcp", Optional = true }]
        };

        List<AITool> tools;
        using (CapabilityEnvelopeAccessor.Begin(Envelope("bundle-a:epr-mcp", "bundle-b:epr-mcp")))
            tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        tools.Should().BeEmpty();
        mcp.Verify(p => p.GetToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static CapabilityEnvelope Envelope(params string[] servers) =>
        new() { AllowedMcpServers = servers };
}

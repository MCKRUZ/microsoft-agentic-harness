using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// Tests for <see cref="ToolCapabilityResolver"/> — the precedence chain (operator override → MCP
/// annotation → first-party declaration/keyword heuristic) and, most importantly, the narrow keyword
/// vocabulary's refusal to classify ordinary tool names.
/// </summary>
public sealed class ToolCapabilityResolverTests
{
    [Theory]
    [InlineData("read_file")]
    [InlineData("search_documents")]
    [InlineData("list_issues")]
    [InlineData("get_user")]
    [InlineData("run_skill_script")]
    [InlineData("load_skill")]
    [InlineData("create_label")]
    [InlineData("update_draft")]
    public void Resolve_CommonBenignNames_ReturnsUnclassified(string toolName)
    {
        // The whole design of the keyword vocabulary in one test. Every name here is deliberately
        // ordinary — the kind of tool that makes up most of any real estate — and none may classify.
        // Mutation to run: add "read" or "write" to the keyword rules; every case here must then fail.
        var resolver = BuildResolver();

        var profile = resolver.Resolve(toolName);

        profile.Capabilities.Should().Be(ToolCompositionCapability.None);
        profile.Origin.Should().Be(ToolCapabilityOrigin.Unclassified);
    }

    [Theory]
    [InlineData("web_fetch", ToolCompositionCapability.IngestsUntrustedInput)]
    [InlineData("browse_page", ToolCompositionCapability.IngestsUntrustedInput)]
    [InlineData("send_email", ToolCompositionCapability.SendsOutbound)]
    [InlineData("run_shell", ToolCompositionCapability.ExecutesCode)]
    [InlineData("read_credential", ToolCompositionCapability.ReadsCredentials)]
    [InlineData("write_file", ToolCompositionCapability.WritesFiles)]
    public void Resolve_ClearlyDangerousNames_ClassifiesByKeyword(string toolName, ToolCompositionCapability expected)
    {
        var resolver = BuildResolver();

        var profile = resolver.Resolve(toolName);

        profile.Capabilities.Should().HaveFlag(expected);
        profile.Origin.Should().Be(ToolCapabilityOrigin.KeywordHeuristic);
    }

    [Fact]
    public void Resolve_FirstPartyDeclaration_BeatsKeywordHeuristic()
    {
        // A tool named like a fetcher but declared otherwise by its own registration must be believed
        // over the keyword guess — the declaration is the stronger signal.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("web_fetch", (_, _) => Mock.Of<ITool>(
            t => t.Capabilities == ToolCompositionCapability.None));
        var resolver = BuildResolver(services.BuildServiceProvider(), registeredKeys: new HashSet<string> { "web_fetch" });

        var profile = resolver.Resolve("web_fetch");

        profile.Origin.Should().Be(ToolCapabilityOrigin.Unclassified);
    }

    [Fact]
    public void Resolve_OperatorToolOverride_BeatsEverythingElse()
    {
        var gating = new ToolCompositionGatingConfig
        {
            ToolCapabilities =
            [
                new ToolCapabilityOverride
                {
                    Tool = "search_pages",
                    Capabilities = [ToolCompositionCapability.IngestsUntrustedInput],
                    Reason = "returns third-party page content despite the benign name",
                },
            ],
        };
        var resolver = BuildResolver(governance: Governance(gating));

        var profile = resolver.Resolve("search_pages");

        profile.Capabilities.Should().Be(ToolCompositionCapability.IngestsUntrustedInput);
        profile.Origin.Should().Be(ToolCapabilityOrigin.OperatorOverride);
    }

    [Fact]
    public void Resolve_ServerOverride_AddsBitsButNeverClearsTheKeywordFinding()
    {
        // A per-server override is additive-only. This is a resolver-level guarantee independent of
        // config shape — the server override config type has no way to express "clear", so the
        // resolver's OR-in behaviour is what the type system already enforces; this test pins that the
        // resolver never subtracts, even if a caller of ToolCapabilityResolver constructed one that did.
        var behaviorRegistry = new ToolBehaviorRegistry(new ServiceCollection().BuildServiceProvider());
        behaviorRegistry.RecordAdvertised("web_fetch", new ToolBehavior(ToolBehaviorSource.UntrustedMcpServer, ServerName: "web"));

        var gating = new ToolCompositionGatingConfig
        {
            ServerCapabilities =
            [
                new ToolCapabilityServerOverride
                {
                    Server = "web",
                    Capabilities = [ToolCompositionCapability.ReadsCredentials],
                    Reason = "this server's tools also relay stored API keys",
                },
            ],
        };
        var resolver = BuildResolver(governance: Governance(gating), behaviorRegistry: behaviorRegistry);

        var profile = resolver.Resolve("web_fetch");

        // The keyword hit (IngestsUntrustedInput, from "fetch") survives, and the server addition
        // (ReadsCredentials) is layered on top — neither replaces the other.
        profile.Capabilities.Should().HaveFlag(ToolCompositionCapability.IngestsUntrustedInput);
        profile.Capabilities.Should().HaveFlag(ToolCompositionCapability.ReadsCredentials);
    }

    [Fact]
    public void Resolve_OpenWorldMcpAnnotation_AddsIngestsUntrustedInput()
    {
        var behaviorRegistry = new ToolBehaviorRegistry(new ServiceCollection().BuildServiceProvider());
        behaviorRegistry.RecordAdvertised(
            "list_records", new ToolBehavior(ToolBehaviorSource.UntrustedMcpServer, OpenWorld: true, ServerName: "crm"));
        var resolver = BuildResolver(behaviorRegistry: behaviorRegistry);

        var profile = resolver.Resolve("list_records");

        profile.Capabilities.Should().HaveFlag(ToolCompositionCapability.IngestsUntrustedInput);
        profile.Origin.Should().Be(ToolCapabilityOrigin.McpAnnotation);
    }

    [Fact]
    public void Resolve_KeywordMatchThenMcpAnnotation_ReportsTheStrongerOrigin()
    {
        // A profile is one record describing possibly-several bits from possibly-several sources, and
        // Origin can only report one of them. When a stronger source (the MCP annotation) contributes
        // on top of a weaker one (the keyword guess), the reported Origin must upgrade — an approver
        // shown "keyword guess" for a bit a real server annotation actually backs is a mislabelled
        // audit trail, not just a cosmetic detail.
        var behaviorRegistry = new ToolBehaviorRegistry(new ServiceCollection().BuildServiceProvider());
        behaviorRegistry.RecordAdvertised(
            "run_shell", new ToolBehavior(ToolBehaviorSource.UntrustedMcpServer, OpenWorld: true, ServerName: "ci"));
        var resolver = BuildResolver(behaviorRegistry: behaviorRegistry);

        var profile = resolver.Resolve("run_shell");

        // "run_shell" keyword-matches ExecutesCode; the OpenWorld hint additionally contributes
        // IngestsUntrustedInput. McpAnnotation outranks KeywordHeuristic, so it must win the label.
        profile.Capabilities.Should().HaveFlag(ToolCompositionCapability.ExecutesCode);
        profile.Capabilities.Should().HaveFlag(ToolCompositionCapability.IngestsUntrustedInput);
        profile.Origin.Should().Be(ToolCapabilityOrigin.McpAnnotation);
    }

    private static ToolCapabilityResolver BuildResolver(
        IServiceProvider? serviceProvider = null,
        GovernanceConfig? governance = null,
        IToolBehaviorRegistry? behaviorRegistry = null,
        IReadOnlySet<string>? registeredKeys = null) =>
        new(
            serviceProvider ?? new ServiceCollection().BuildServiceProvider(),
            behaviorRegistry ?? new ToolBehaviorRegistry(new ServiceCollection().BuildServiceProvider()),
            Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == (governance ?? new GovernanceConfig())),
            registeredKeys ?? new HashSet<string>());

    private static GovernanceConfig Governance(ToolCompositionGatingConfig gating) =>
        new() { ToolCompositionGating = gating };
}

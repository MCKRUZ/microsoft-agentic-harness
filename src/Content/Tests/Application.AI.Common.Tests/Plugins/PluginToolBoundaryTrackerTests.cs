using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Services.Plugins;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Moq;

namespace Application.AI.Common.Tests.Plugins;

/// <summary>
/// #524: <see cref="PluginToolBoundaryTracker"/> is what turns a plugin
/// <c>AllowedTools</c>/<c>DeniedTools</c> entry that matches no real tool from a silent no-op into
/// a loud, fail-closed fault — either immediately at startup (no MCP server is configured anywhere
/// on the host) or lazily, once every host-configured MCP server has reported its tool list at
/// least once. Deliberately keyed on the HOST'S full server list, not any one plugin's own declared
/// servers — a review-round finding confirmed a plugin skill's tool declaration can resolve against
/// ANY host-configured server (<c>ToolChainBuilder.ResolveEffectiveMcpServerName</c>), so a plugin
/// with zero MCP servers of its own can still legitimately reference a host-level server's tool.
/// </summary>
public sealed class PluginToolBoundaryTrackerTests
{
    private readonly Mock<IPluginRegistry> _registry = new();
    private readonly PluginToolBoundaryTracker _sut;

    public PluginToolBoundaryTrackerTests()
    {
        _sut = new PluginToolBoundaryTracker(_registry.Object);
    }

    private static LoadedPlugin MakePlugin(
        string name, IReadOnlyList<string>? mcpServerNames = null,
        IReadOnlyList<string>? allowedTools = null, IReadOnlyList<string>? deniedTools = null) =>
        new(name, "1.0.0", $"/plugins/{name}", new PluginManifest { Name = name, Version = "1.0.0" },
            PluginLoadStatus.Loaded, [], mcpServerNames ?? [],
            new PluginDeclaration { Name = name, AllowedTools = allowedTools, DeniedTools = deniedTools });

    private static bool NoFirstPartyToolsKnown(string _) => false;

    private static readonly IReadOnlyCollection<string> NoServersConfigured = [];

    [Fact]
    public void Seed_NoServersConfiguredAnywhereAndUnknownDeniedEntry_ReturnsImmediateViolation()
    {
        var plugin = MakePlugin("azure", deniedTools: ["file_wrte"]);

        var violations = _sut.Seed([plugin], NoFirstPartyToolsKnown, NoServersConfigured);

        violations.Should().ContainSingle(v =>
            v.PluginName == "azure" && v.ListKind == "DeniedTools" && v.ToolName == "file_wrte");
    }

    [Fact]
    public void Seed_NoServersConfiguredAndEveryEntryKnownFirstParty_ReturnsNoViolations()
    {
        var plugin = MakePlugin("azure", deniedTools: ["file_write"]);

        var violations = _sut.Seed([plugin], name => name == "file_write", NoServersConfigured);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Seed_PluginDeclaresNoOwnServerButHostHasOneConfigured_DoesNotFaultImmediately()
    {
        // The regression this test guards: a plugin with zero MCP servers of its OWN can still
        // legitimately reference a tool from a host-level MCP server it doesn't declare (a plugin
        // skill's ToolDeclaration resolves against any configured server when unrestricted). Faulting
        // this immediately — as an earlier version did, scoped only to the plugin's own servers —
        // crashed boot on a valid, pre-existing configuration.
        var plugin = MakePlugin("azure", deniedTools: ["maybe_host_level_tool"]); // no own MCP servers

        var violations = _sut.Seed([plugin], NoFirstPartyToolsKnown, ["host:github"]);

        violations.Should().BeEmpty();
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ReportServerToolsDiscovered_HostServerResolvesAPluginsEntry_NeverFaults()
    {
        // Same scenario, carried through to resolution: the host-level server (not the plugin's own)
        // reports a list containing the entry — it resolves, exactly as a valid config should.
        var plugin = MakePlugin("azure", deniedTools: ["delete_repository"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["host:github"]);

        var violations = _sut.ReportServerToolsDiscovered("host:github", ["delete_repository", "create_issue"]);

        violations.Should().BeEmpty();
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Seed_NoServersConfiguredAndSameUnknownNameInBothLists_ReturnsOneImmediateViolationInsteadOfThrowing()
    {
        var plugin = MakePlugin("azure", allowedTools: ["file_wrte"], deniedTools: ["file_wrte"]);

        var violations = _sut.Seed([plugin], NoFirstPartyToolsKnown, NoServersConfigured);

        violations.Should().ContainSingle(v => v.PluginName == "azure" && v.ToolName == "file_wrte");
    }

    [Fact]
    public void Seed_HasConfiguredServerAndSameUnknownNameInBothLists_DoesNotThrow()
    {
        // Regression: a name appearing in both AllowedTools and DeniedTools (or twice in one list)
        // used to throw ArgumentException out of the pending-entries dictionary build the moment at
        // least one MCP server exists, crashing host startup on a legally shaped — if pointless —
        // plugin config, instead of the clean diagnostic this feature exists to produce.
        var plugin = MakePlugin("azure", allowedTools: ["file_wrte"], deniedTools: ["file_wrte"]);

        var act = () => _sut.Seed([plugin], NoFirstPartyToolsKnown, ["server1"]);

        act.Should().NotThrow();
    }

    [Fact]
    public void Seed_HasConfiguredServerAndSameUnknownNameTwiceInOneList_ThenReportServerToolsDiscovered_FaultsOnce()
    {
        var plugin = MakePlugin("azure", deniedTools: ["file_wrte", "file_wrte"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["server1"]);

        var violations = _sut.ReportServerToolsDiscovered("server1", ["unrelated"]);

        violations.Should().ContainSingle(v => v.ToolName == "file_wrte");
        _registry.Verify(r => r.MarkBoundaryFaulted("azure", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void Seed_AtLeastOneServerConfiguredAndUnknownEntry_DoesNotReturnImmediateViolation()
    {
        // Not yet decidable — the entry might be a real MCP tool name, only knowable once that
        // server's tool list has actually been discovered (ReportServerToolsDiscovered).
        var plugin = MakePlugin("azure", deniedTools: ["maybe_mcp_tool"]);

        var violations = _sut.Seed([plugin], NoFirstPartyToolsKnown, ["server1"]);

        violations.Should().BeEmpty();
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ReportServerToolsDiscovered_ServerListContainsThePendingEntry_ResolvesWithoutFault()
    {
        var plugin = MakePlugin("azure", deniedTools: ["real_tool"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["server1"]);

        var violations = _sut.ReportServerToolsDiscovered("server1", ["real_tool", "other_tool"]);

        violations.Should().BeEmpty();
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ReportServerToolsDiscovered_ServerListMissingThePendingEntry_FaultsAndReturnsViolation()
    {
        var plugin = MakePlugin("azure", deniedTools: ["file_wrte"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["server1"]);

        var violations = _sut.ReportServerToolsDiscovered("server1", ["some_other_tool"]);

        violations.Should().ContainSingle(v =>
            v.PluginName == "azure" && v.ListKind == "DeniedTools" && v.ToolName == "file_wrte");
        _registry.Verify(r => r.MarkBoundaryFaulted("azure", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void ReportServerToolsDiscovered_TwoConfiguredServers_OnlyFaultsAfterTheLastOneReports()
    {
        var plugin = MakePlugin("azure", deniedTools: ["file_wrte"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["s1", "s2"]);

        var afterFirst = _sut.ReportServerToolsDiscovered("s1", ["unrelated"]);
        afterFirst.Should().BeEmpty("s2 hasn't reported yet — not provably fake");
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        var afterSecond = _sut.ReportServerToolsDiscovered("s2", ["also_unrelated"]);
        afterSecond.Should().ContainSingle(v => v.ToolName == "file_wrte");
        _registry.Verify(r => r.MarkBoundaryFaulted("azure", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void ReportServerToolsDiscovered_TwoConfiguredServersOneResolvesTheEntry_NeverFaults()
    {
        var plugin = MakePlugin("azure", deniedTools: ["real_tool"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["s1", "s2"]);

        _sut.ReportServerToolsDiscovered("s1", ["unrelated"]).Should().BeEmpty();
        var afterSecond = _sut.ReportServerToolsDiscovered("s2", ["real_tool"]);

        afterSecond.Should().BeEmpty();
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ReportServerToolsDiscovered_ConcurrentReportsForBothServers_FaultsExactlyOnce()
    {
        var plugin = MakePlugin("azure", deniedTools: ["file_wrte"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["s1", "s2"]);

        var results = new System.Collections.Concurrent.ConcurrentBag<IReadOnlyList<PluginToolBoundaryViolation>>();
        Parallel.Invoke(
            () => results.Add(_sut.ReportServerToolsDiscovered("s1", ["unrelated1"])),
            () => results.Add(_sut.ReportServerToolsDiscovered("s2", ["unrelated2"])));

        results.SelectMany(r => r).Should().ContainSingle(v => v.ToolName == "file_wrte");
        _registry.Verify(r => r.MarkBoundaryFaulted("azure", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void ReportServerToolsDiscovered_ConcurrentReportsForTheSameSingleServer_FaultsExactlyOnce()
    {
        // Regression (code-review finding on #524): two overlapping discovery calls for the SAME
        // server can both pass the initial lookup before either removes the plugin from tracking,
        // so both would reach the "last pending server just reported" branch and double-fault
        // without the Resolved guard.
        var plugin = MakePlugin("azure", deniedTools: ["file_wrte"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["s1"]);

        var results = new System.Collections.Concurrent.ConcurrentBag<IReadOnlyList<PluginToolBoundaryViolation>>();
        Parallel.Invoke(
            () => results.Add(_sut.ReportServerToolsDiscovered("s1", ["unrelated"])),
            () => results.Add(_sut.ReportServerToolsDiscovered("s1", ["unrelated"])));

        results.SelectMany(r => r).Should().ContainSingle(v => v.ToolName == "file_wrte");
        _registry.Verify(r => r.MarkBoundaryFaulted("azure", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void ReportServerToolsDiscovered_ServerNothingIsPendingFor_ReturnsEmpty()
    {
        var violations = _sut.ReportServerToolsDiscovered("unrelated:server", ["tool1"]);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void ReportServerToolsDiscovered_MatchIsCaseInsensitive_ResolvesWithoutFault()
    {
        var plugin = MakePlugin("azure", deniedTools: ["Real_Tool"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["server1"]);

        var violations = _sut.ReportServerToolsDiscovered("server1", ["real_tool"]);

        violations.Should().BeEmpty();
    }
}

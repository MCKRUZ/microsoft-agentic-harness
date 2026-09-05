using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Services.Plugins;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Moq;

namespace Application.AI.Common.Tests.Plugins;

/// <summary>
/// #524: <see cref="PluginToolBoundaryTracker"/> is what turns a plugin
/// <c>AllowedTools</c>/<c>DeniedTools</c> entry that matches no real tool from a silent no-op into
/// a loud, fail-closed fault — either immediately at startup (a plugin with no MCP servers has no
/// other source an entry could resolve from) or lazily, once every MCP server the plugin depends on
/// has reported its tool list at least once.
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

    [Fact]
    public void Seed_PluginWithNoMcpServersAndUnknownDeniedEntry_ReturnsImmediateViolation()
    {
        var plugin = MakePlugin("azure", deniedTools: ["file_wrte"]);

        var violations = _sut.Seed([plugin], NoFirstPartyToolsKnown);

        violations.Should().ContainSingle(v =>
            v.PluginName == "azure" && v.ListKind == "DeniedTools" && v.ToolName == "file_wrte");
    }

    [Fact]
    public void Seed_PluginWithNoMcpServersAndEveryEntryKnownFirstParty_ReturnsNoViolations()
    {
        var plugin = MakePlugin("azure", deniedTools: ["file_write"]);

        var violations = _sut.Seed([plugin], name => name == "file_write");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Seed_PluginWithMcpServerAndUnknownEntry_DoesNotReturnImmediateViolation()
    {
        // Not yet decidable — the entry might be a real MCP tool name, only knowable once that
        // server's tool list has actually been discovered (ReportServerToolsDiscovered).
        var plugin = MakePlugin("azure", mcpServerNames: ["azure:server1"], deniedTools: ["maybe_mcp_tool"]);

        var violations = _sut.Seed([plugin], NoFirstPartyToolsKnown);

        violations.Should().BeEmpty();
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ReportServerToolsDiscovered_ServerListContainsThePendingEntry_ResolvesWithoutFault()
    {
        var plugin = MakePlugin("azure", mcpServerNames: ["azure:server1"], deniedTools: ["real_tool"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown);

        var violations = _sut.ReportServerToolsDiscovered("azure:server1", ["real_tool", "other_tool"]);

        violations.Should().BeEmpty();
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ReportServerToolsDiscovered_ServerListMissingThePendingEntry_FaultsAndReturnsViolation()
    {
        var plugin = MakePlugin("azure", mcpServerNames: ["azure:server1"], deniedTools: ["file_wrte"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown);

        var violations = _sut.ReportServerToolsDiscovered("azure:server1", ["some_other_tool"]);

        violations.Should().ContainSingle(v =>
            v.PluginName == "azure" && v.ListKind == "DeniedTools" && v.ToolName == "file_wrte");
        _registry.Verify(r => r.MarkBoundaryFaulted("azure", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void ReportServerToolsDiscovered_TwoServers_OnlyFaultsAfterTheLastOneReports()
    {
        var plugin = MakePlugin("azure", mcpServerNames: ["azure:s1", "azure:s2"], deniedTools: ["file_wrte"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown);

        var afterFirst = _sut.ReportServerToolsDiscovered("azure:s1", ["unrelated"]);
        afterFirst.Should().BeEmpty("s2 hasn't reported yet — not provably fake");
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        var afterSecond = _sut.ReportServerToolsDiscovered("azure:s2", ["also_unrelated"]);
        afterSecond.Should().ContainSingle(v => v.ToolName == "file_wrte");
        _registry.Verify(r => r.MarkBoundaryFaulted("azure", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void ReportServerToolsDiscovered_TwoServersOneResolvesTheEntry_NeverFaults()
    {
        var plugin = MakePlugin("azure", mcpServerNames: ["azure:s1", "azure:s2"], deniedTools: ["real_tool"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown);

        _sut.ReportServerToolsDiscovered("azure:s1", ["unrelated"]).Should().BeEmpty();
        var afterSecond = _sut.ReportServerToolsDiscovered("azure:s2", ["real_tool"]);

        afterSecond.Should().BeEmpty();
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ReportServerToolsDiscovered_ConcurrentReportsForBothServers_FaultsExactlyOnce()
    {
        var plugin = MakePlugin("azure", mcpServerNames: ["azure:s1", "azure:s2"], deniedTools: ["file_wrte"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown);

        var results = new System.Collections.Concurrent.ConcurrentBag<IReadOnlyList<PluginToolBoundaryViolation>>();
        Parallel.Invoke(
            () => results.Add(_sut.ReportServerToolsDiscovered("azure:s1", ["unrelated1"])),
            () => results.Add(_sut.ReportServerToolsDiscovered("azure:s2", ["unrelated2"])));

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
        var plugin = MakePlugin("azure", mcpServerNames: ["azure:server1"], deniedTools: ["Real_Tool"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown);

        var violations = _sut.ReportServerToolsDiscovered("azure:server1", ["real_tool"]);

        violations.Should().BeEmpty();
    }
}

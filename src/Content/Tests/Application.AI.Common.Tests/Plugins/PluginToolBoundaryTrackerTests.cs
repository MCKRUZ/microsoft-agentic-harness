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
    public void Seed_ImmediateViolation_AlsoMarksTheRegistryFaulted()
    {
        // Defense in depth (review-round finding): the immediate branch's enforcement otherwise
        // relies entirely on the caller (PluginToolBoundaryStartupValidator) rethrowing — marking
        // the registry here too means IPluginRegistry.IsBoundaryFaulted is authoritative regardless.
        var plugin = MakePlugin("azure", deniedTools: ["file_wrte"]);

        _sut.Seed([plugin], NoFirstPartyToolsKnown, NoServersConfigured);

        _registry.Verify(r => r.MarkBoundaryFaulted("azure", It.IsAny<IReadOnlyList<PluginToolBoundaryViolation>>()), Times.Once);
    }

    [Fact]
    public void Seed_ImmediateViolation_PassesTheActualViolationListToTheRegistry()
    {
        // #608: MarkBoundaryFaulted's violations argument must be the real, computed list -- not
        // discarded -- so a consumer can later distinguish a DeniedTools fault from an
        // AllowedTools-only one via IPluginRegistry.GetBoundaryViolations.
        var plugin = MakePlugin("azure", deniedTools: ["file_wrte"]);

        _sut.Seed([plugin], NoFirstPartyToolsKnown, NoServersConfigured);

        _registry.Verify(r => r.MarkBoundaryFaulted(
            "azure",
            It.Is<IReadOnlyList<PluginToolBoundaryViolation>>(v =>
                v.Count == 1
                && v[0].PluginName == "azure"
                && v[0].ListKind == PluginToolBoundaryListKind.DeniedTools
                && v[0].ToolName == "file_wrte")),
            Times.Once);
    }

    [Fact]
    public void Seed_NoServersConfiguredAndEveryEntryKnownFirstParty_ReturnsNoViolations()
    {
        var plugin = MakePlugin("azure", deniedTools: ["file_write"]);

        var violations = _sut.Seed([plugin], name => name == "file_write", NoServersConfigured);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Seed_EveryEntryKnownFirstParty_ExplicitlyMarksTheRegistryVerified()
    {
        // #613: a plugin whose boundary is already fully decidable at Seed time used to just be
        // skipped (no registry call at all), leaving GetBoundaryStatus fall through to its default —
        // which meant "proven safe" and "never checked" were indistinguishable. Seed must be the one
        // place that positively proves this plugin's boundary, not rely on an absent entry to imply it.
        var plugin = MakePlugin("azure", deniedTools: ["file_write"]);

        _sut.Seed([plugin], name => name == "file_write", NoServersConfigured);

        _registry.Verify(r => r.MarkBoundaryVerified("azure"), Times.Once);
    }

    [Fact]
    public void Seed_PluginDeclaresNoBoundaryAtAll_ExplicitlyMarksTheRegistryVerified()
    {
        // Same fix, the far more common real-world shape: most plugins declare neither AllowedTools
        // nor DeniedTools at all. BoundaryEntries is empty, so the old code's "nothing unresolved"
        // early-continue never distinguished this from "never seeded" either.
        var plugin = MakePlugin("azure");

        _sut.Seed([plugin], NoFirstPartyToolsKnown, NoServersConfigured);

        _registry.Verify(r => r.MarkBoundaryVerified("azure"), Times.Once);
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
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<IReadOnlyList<PluginToolBoundaryViolation>>()), Times.Never);
    }

    [Fact]
    public void Seed_EntryDeferredToLazyResolution_MarksTheRegistryPending()
    {
        // #524 redesign: an entry that can't be resolved immediately must be recorded as Pending
        // (untrusted) — not left implicitly "not yet faulted, so trusted" the way a bare fault flag
        // would leave it. This is what lets ToolChainBuilder deny the plugin's tools while it waits,
        // instead of a server nothing organically queries leaving it silently trusted forever.
        var plugin = MakePlugin("azure", deniedTools: ["maybe_host_level_tool"]);

        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["host:github"]);

        _registry.Verify(r => r.MarkBoundaryPending("azure"), Times.Once);
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
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<IReadOnlyList<PluginToolBoundaryViolation>>()), Times.Never);
    }

    [Fact]
    public void ReportServerToolsDiscovered_EveryPendingEntryResolves_MarksTheRegistryVerified()
    {
        // The other half of the Pending -> {Verified, Faulted} transition (#524 redesign): resolving
        // clean must explicitly clear Pending, not just leave it implicitly "not faulted" — that
        // implicit gap is exactly what let a resolved plugin's boundary stay ambiguous with one that
        // was never checked at all.
        var plugin = MakePlugin("azure", deniedTools: ["delete_repository"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["host:github"]);

        _sut.ReportServerToolsDiscovered("host:github", ["delete_repository", "create_issue"]);

        _registry.Verify(r => r.MarkBoundaryVerified("azure"), Times.Once);
    }

    [Fact]
    public void PendingServerNames_AfterSeed_ContainsEveryConfiguredServer()
    {
        // Matches Seed's own deliberate design (see class remarks): an entry can resolve against ANY
        // host-configured server, not just the plugin's own, so every configured server counts as
        // "pending" for PluginToolBoundaryStartupValidator's proactive-resolution purposes.
        var plugin = MakePlugin("azure", deniedTools: ["maybe_host_level_tool"]);

        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["host:github", "host:jira"]);

        _sut.PendingServerNames.Should().BeEquivalentTo(["host:github", "host:jira"]);
    }

    [Fact]
    public void PendingServerNames_NoPluginHasUnresolvedEntries_IsEmpty()
    {
        var plugin = MakePlugin("azure", deniedTools: ["file_write"]);

        _sut.Seed([plugin], name => name == "file_write", ["host:github"]);

        _sut.PendingServerNames.Should().BeEmpty();
    }

    [Fact]
    public void ReportServerToolsDiscovered_EveryEntryResolvesFromTheFirstOfTwoServers_VerifiesWithoutWaitingForTheSecond()
    {
        // #524 round-2 code-review: waiting for every configured server to report before verifying a
        // plugin whose entries already fully resolved from an EARLIER server needlessly stretches how
        // long ToolChainBuilder denies its tools (Pending) — a later, unrelated server's report can
        // never un-match something already proven to exist.
        var plugin = MakePlugin("azure", deniedTools: ["delete_repository"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["host:github", "host:jira"]);

        var violations = _sut.ReportServerToolsDiscovered("host:github", ["delete_repository"]);

        violations.Should().BeEmpty();
        _registry.Verify(r => r.MarkBoundaryVerified("azure"), Times.Once);
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<IReadOnlyList<PluginToolBoundaryViolation>>()), Times.Never);

        // The still-unreported "host:jira" server must have nothing left to do for this plugin.
        var laterReport = _sut.ReportServerToolsDiscovered("host:jira", []);
        laterReport.Should().BeEmpty();
        _registry.Verify(r => r.MarkBoundaryVerified("azure"), Times.Once, "must not be called a second time for the same resolution");
    }

    [Fact]
    public void Seed_NoServersConfiguredAndSameUnknownNameInBothLists_ReturnsOneImmediateViolationInsteadOfThrowing()
    {
        var plugin = MakePlugin("azure", allowedTools: ["file_wrte"], deniedTools: ["file_wrte"]);

        var violations = _sut.Seed([plugin], NoFirstPartyToolsKnown, NoServersConfigured);

        violations.Should().ContainSingle(v => v.PluginName == "azure" && v.ToolName == "file_wrte");
    }

    [Fact]
    public void Seed_SameUnknownNameInBothLists_ReportsDeniedToolsNotAllowedTools()
    {
        // The bypass-immune guarantee lives in DeniedTools, so a duplicate-list typo must surface
        // under that label, not silently as an AllowedTools violation an operator might "fix" by
        // only touching the AllowedTools entry and leaving the DeniedTools occurrence unaddressed.
        var plugin = MakePlugin("azure", allowedTools: ["file_wrte"], deniedTools: ["file_wrte"]);

        var violations = _sut.Seed([plugin], NoFirstPartyToolsKnown, NoServersConfigured);

        violations.Should().ContainSingle(v => v.ListKind == "DeniedTools");
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
        _registry.Verify(r => r.MarkBoundaryFaulted("azure", It.IsAny<IReadOnlyList<PluginToolBoundaryViolation>>()), Times.Once);
    }

    [Fact]
    public void Seed_AtLeastOneServerConfiguredAndUnknownEntry_DoesNotReturnImmediateViolation()
    {
        // Not yet decidable — the entry might be a real MCP tool name, only knowable once that
        // server's tool list has actually been discovered (ReportServerToolsDiscovered).
        var plugin = MakePlugin("azure", deniedTools: ["maybe_mcp_tool"]);

        var violations = _sut.Seed([plugin], NoFirstPartyToolsKnown, ["server1"]);

        violations.Should().BeEmpty();
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<IReadOnlyList<PluginToolBoundaryViolation>>()), Times.Never);
    }

    [Fact]
    public void ReportServerToolsDiscovered_ServerListContainsThePendingEntry_ResolvesWithoutFault()
    {
        var plugin = MakePlugin("azure", deniedTools: ["real_tool"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["server1"]);

        var violations = _sut.ReportServerToolsDiscovered("server1", ["real_tool", "other_tool"]);

        violations.Should().BeEmpty();
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<IReadOnlyList<PluginToolBoundaryViolation>>()), Times.Never);
    }

    [Fact]
    public void ReportServerToolsDiscovered_ServerListMissingThePendingEntry_FaultsAndReturnsViolation()
    {
        var plugin = MakePlugin("azure", deniedTools: ["file_wrte"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["server1"]);

        var violations = _sut.ReportServerToolsDiscovered("server1", ["some_other_tool"]);

        violations.Should().ContainSingle(v =>
            v.PluginName == "azure" && v.ListKind == "DeniedTools" && v.ToolName == "file_wrte");
        _registry.Verify(r => r.MarkBoundaryFaulted("azure", It.IsAny<IReadOnlyList<PluginToolBoundaryViolation>>()), Times.Once);
    }

    [Fact]
    public void ReportServerToolsDiscovered_LastPendingServerReports_PassesTheActualViolationListToTheRegistry()
    {
        // #608: the lazy fault path (unlike Seed's immediate path above) builds its violation list
        // from PendingEntries at resolution time -- must confirm it's threaded through too, not just
        // the immediate branch.
        var plugin = MakePlugin("azure", allowedTools: ["typo_tool"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["server1"]);

        _sut.ReportServerToolsDiscovered("server1", ["unrelated"]);

        _registry.Verify(r => r.MarkBoundaryFaulted(
            "azure",
            It.Is<IReadOnlyList<PluginToolBoundaryViolation>>(v =>
                v.Count == 1
                && v[0].PluginName == "azure"
                && v[0].ListKind == PluginToolBoundaryListKind.AllowedTools
                && v[0].ToolName == "typo_tool")),
            Times.Once);
    }

    [Fact]
    public void ReportServerToolsDiscovered_TwoConfiguredServers_OnlyFaultsAfterTheLastOneReports()
    {
        var plugin = MakePlugin("azure", deniedTools: ["file_wrte"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["s1", "s2"]);

        var afterFirst = _sut.ReportServerToolsDiscovered("s1", ["unrelated"]);
        afterFirst.Should().BeEmpty("s2 hasn't reported yet — not provably fake");
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<IReadOnlyList<PluginToolBoundaryViolation>>()), Times.Never);

        var afterSecond = _sut.ReportServerToolsDiscovered("s2", ["also_unrelated"]);
        afterSecond.Should().ContainSingle(v => v.ToolName == "file_wrte");
        _registry.Verify(r => r.MarkBoundaryFaulted("azure", It.IsAny<IReadOnlyList<PluginToolBoundaryViolation>>()), Times.Once);
    }

    [Fact]
    public void ReportServerToolsDiscovered_TwoConfiguredServersOneResolvesTheEntry_NeverFaults()
    {
        var plugin = MakePlugin("azure", deniedTools: ["real_tool"]);
        _sut.Seed([plugin], NoFirstPartyToolsKnown, ["s1", "s2"]);

        _sut.ReportServerToolsDiscovered("s1", ["unrelated"]).Should().BeEmpty();
        var afterSecond = _sut.ReportServerToolsDiscovered("s2", ["real_tool"]);

        afterSecond.Should().BeEmpty();
        _registry.Verify(r => r.MarkBoundaryFaulted(It.IsAny<string>(), It.IsAny<IReadOnlyList<PluginToolBoundaryViolation>>()), Times.Never);
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
        _registry.Verify(r => r.MarkBoundaryFaulted("azure", It.IsAny<IReadOnlyList<PluginToolBoundaryViolation>>()), Times.Once);
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
        _registry.Verify(r => r.MarkBoundaryFaulted("azure", It.IsAny<IReadOnlyList<PluginToolBoundaryViolation>>()), Times.Once);
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

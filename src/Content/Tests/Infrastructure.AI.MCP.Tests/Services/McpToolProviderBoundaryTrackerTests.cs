using Application.AI.Common.Interfaces.Plugins;
using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// #524: <see cref="McpToolProvider"/> reports every successfully-discovered MCP server's tool
/// names to <see cref="IPluginToolBoundaryTracker"/> — the untrusted-input half of the plugin
/// tool-boundary existence check (the other half, first-party names at startup, is covered by
/// <c>PluginToolBoundaryStartupValidator</c>'s own tests).
/// </summary>
/// <remarks>
/// No fixture in this test project drives <c>McpToolProvider.DiscoverToolsAsync</c>'s
/// success path end to end — it requires a live <c>McpClient</c> connection, which nothing here
/// fakes (every existing <c>McpToolProvider*Tests</c> file exercises only failure/unavailable-server
/// paths). These tests instead call
/// <see cref="McpToolProvider.ReportDiscoveryToBoundaryTracker(string, IEnumerable{string})"/>
/// directly — the exact reporting step <c>DiscoverToolsAsync</c> calls immediately after a
/// successful <c>ListToolsAsync</c>, extracted to take bare tool names specifically so it can be
/// exercised without a real connection. A correctness-review finding on #524 flagged this call site
/// as having zero coverage: deleting it would silently re-open the exact "unrecognized DeniedTools
/// entry is a silent no-op" hole the feature exists to close, with every other test in the suite
/// staying green.
/// </remarks>
public sealed class McpToolProviderBoundaryTrackerTests
{
    private static (McpToolProvider Provider, McpConnectionManager Manager) CreateSut(
        IPluginToolBoundaryTracker? boundaryTracker)
    {
        var manager = McpConnectionManagerBundleEgressSupport.CreateManager(
            Mock.Of<ILogger<McpConnectionManager>>(),
            new Mock<ILoggerFactory>().Object,
            TestSsrf.HandlerFactory(),
            new Domain.Common.Config.AI.MCP.McpServersConfig(),
            new Infrastructure.AI.Bundles.BundleOwnedMcpServerRegistry());

        var provider = new McpToolProvider(Mock.Of<ILogger<McpToolProvider>>(), manager, boundaryTracker);
        return (provider, manager);
    }

    [Fact]
    public void ReportDiscoveryToBoundaryTracker_TrackerWired_CallsReportServerToolsDiscoveredWithTheDiscoveredNames()
    {
        var tracker = new Mock<IPluginToolBoundaryTracker>();
        tracker.Setup(t => t.ReportServerToolsDiscovered(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns([]);
        var (sut, _) = CreateSut(tracker.Object);

        var expectedNames = new[] { "tool_a", "tool_b" };
        sut.ReportDiscoveryToBoundaryTracker("azure:server1", expectedNames);

        tracker.Verify(t => t.ReportServerToolsDiscovered(
            "azure:server1",
            It.Is<IReadOnlyCollection<string>>(names => names.SequenceEqual(expectedNames))),
            Times.Once);
    }

    [Fact]
    public void ReportDiscoveryToBoundaryTracker_NoTrackerWired_DoesNotThrow()
    {
        var (sut, _) = CreateSut(boundaryTracker: null);

        var act = () => sut.ReportDiscoveryToBoundaryTracker("azure:server1", ["tool_a"]);

        act.Should().NotThrow();
    }

    [Fact]
    public void ReportDiscoveryToBoundaryTracker_TrackerReturnsAViolation_DoesNotThrow()
    {
        // The violation is logged (LogCritical), never rethrown — a boundary fault must not turn an
        // otherwise-successful discovery call into a failure. Enforcement is separate, via
        // IPluginRegistry.IsBoundaryFaulted on the plugin's next tool resolution.
        var tracker = new Mock<IPluginToolBoundaryTracker>();
        tracker.Setup(t => t.ReportServerToolsDiscovered(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns([new PluginToolBoundaryViolation("azure", "DeniedTools", "file_wrte")]);
        var (sut, _) = CreateSut(tracker.Object);

        var act = () => sut.ReportDiscoveryToBoundaryTracker("azure:server1", ["unrelated"]);

        act.Should().NotThrow();
    }

    [Fact]
    public void DiscoverToolsAsync_SourceStillCallsReportDiscoveryToBoundaryTracker()
    {
        // The three tests above prove ReportDiscoveryToBoundaryTracker itself works — none of them
        // prove DiscoverToolsAsync (private, reachable only via a live MCP connection nothing in
        // this project fakes) still CALLS it after a successful ListToolsAsync. A source-level check
        // is the only thing that can catch that call site being silently removed, matching this
        // repo's established pattern for exactly this class of risk (see SecurityControlHasACallerTests).
        var path = RepoRoot.Combine(
            "src", "Content", "Infrastructure", "Infrastructure.AI.MCP", "Services", "McpToolProvider.cs");
        var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(path));

        var discoverToolsAsyncStart = code.IndexOf("private async Task<IList<AITool>> DiscoverToolsAsync", StringComparison.Ordinal);
        discoverToolsAsyncStart.Should().BeGreaterThan(-1, "DiscoverToolsAsync should still exist under this name");
        var methodBody = code[discoverToolsAsyncStart..(discoverToolsAsyncStart + 1500)];

        methodBody.Should().Contain("ReportDiscoveryToBoundaryTracker(serverName");
    }
}

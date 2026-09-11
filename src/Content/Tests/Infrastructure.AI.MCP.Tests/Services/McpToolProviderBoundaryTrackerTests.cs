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
        IPluginToolBoundaryTracker? boundaryTracker,
        Domain.Common.Config.AI.MCP.McpServersConfig? config = null)
    {
        var manager = McpConnectionManagerBundleEgressSupport.CreateManager(
            Mock.Of<ILogger<McpConnectionManager>>(),
            new Mock<ILoggerFactory>().Object,
            TestSsrf.HandlerFactory(),
            config ?? new Domain.Common.Config.AI.MCP.McpServersConfig(),
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
            .Returns([new PluginToolBoundaryViolation("azure", PluginToolBoundaryListKind.DeniedTools, "file_wrte")]);
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

    [Fact]
    public void DiscoverToolsAsync_SourceDoesNotReportToBoundaryTrackerOnItsOwnFailure()
    {
        // #524 round-2 code-review: DiscoverToolsAsync is called for BOTH the first attempt and the
        // post-reconnect retry. Reporting from its own catch(Exception) block fired on the first,
        // possibly-transient failure — before RetryAfterReconnectAsync ever got a chance to recover —
        // and ReportServerToolsDiscovered only honors the FIRST report per server, silently dropping
        // the correct, later one. A one-off network blip could then permanently fault a healthy
        // plugin. This proves the report call was removed from this method's own failure path.
        var path = RepoRoot.Combine(
            "src", "Content", "Infrastructure", "Infrastructure.AI.MCP", "Services", "McpToolProvider.cs");
        var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(path));

        var start = code.IndexOf("private async Task<IList<AITool>> DiscoverToolsAsync", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "DiscoverToolsAsync should still exist under this name");
        // Success path legitimately still reports (with the real discovered names) — only the
        // catch(Exception) block, where the round-1 report-on-failure bug lived, must be checked.
        var catchStart = code.IndexOf("catch (Exception)", start, StringComparison.Ordinal);
        catchStart.Should().BeGreaterThan(start, "DiscoverToolsAsync should still have a catch(Exception) block");
        // #524 round-2 code-review: searching only for "private " skipped straight past
        // ReportDiscoveryToBoundaryTracker (internal), silently widening the checked region to
        // whatever comes after it too — today's pass was luck, not a guarantee. Take whichever of
        // "private "/"internal "/"public " comes first after the catch block starts.
        var candidates = new[] { "private ", "internal ", "public " }
            .Select(modifier => code.IndexOf(modifier, catchStart + 1, StringComparison.Ordinal))
            .Where(i => i > catchStart)
            .ToList();
        candidates.Should().NotBeEmpty("some member must follow DiscoverToolsAsync's catch block");
        var nextMember = candidates.Min();
        var catchBody = code[catchStart..nextMember];

        catchBody.Should().NotContain("SafeReportDiscoveryToBoundaryTracker",
            "reporting on failure must happen only at the operation's genuinely terminal exits, not on this method's own (possibly first-attempt, possibly-recoverable) failure");
    }

    [Fact]
    public void RetryAfterReconnectAsync_SourceReportsEmptyDiscoveryAtBothTerminalFailureExits()
    {
        // The other half of the fix: reporting must happen exactly once, at the genuinely terminal
        // points — after a failed reconnect, and after a retried discovery also fails — not before.
        var path = RepoRoot.Combine(
            "src", "Content", "Infrastructure", "Infrastructure.AI.MCP", "Services", "McpToolProvider.cs");
        var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(path));

        var start = code.IndexOf("private async Task<IList<AITool>> RetryAfterReconnectAsync", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "RetryAfterReconnectAsync should still exist under this name");
        // Bounded at the helper's own declaration, not the next Task-returning method — that
        // declaration line itself contains "SafeReportDiscoveryToBoundaryTracker" as its method name,
        // which would inflate the occurrence count by one if included in the slice.
        var nextMethod = code.IndexOf("private void SafeReportDiscoveryToBoundaryTracker", start + 1, StringComparison.Ordinal);
        nextMethod.Should().BeGreaterThan(start);
        var methodBody = code[start..nextMethod];

        var occurrences = System.Text.RegularExpressions.Regex.Matches(methodBody, "SafeReportDiscoveryToBoundaryTracker").Count;
        occurrences.Should().Be(2,
            "one for the failed-reconnect exit (freshClient is null) and one for the retried-discovery-also-failed exit");
    }

    [Fact]
    public void GetToolsAsync_SourceSkipsReportingWhenNullClientCameFromCancellation()
    {
        // TryConnectAsync collapses a genuine connection failure and a caller cancellation to the same
        // null return — reporting unconditionally there would let an unrelated caller hitting cancel
        // wrongly fault a plugin's boundary. This proves the cancellation guard is in place.
        var path = RepoRoot.Combine(
            "src", "Content", "Infrastructure", "Infrastructure.AI.MCP", "Services", "McpToolProvider.cs");
        var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(path));

        var start = code.IndexOf("public async Task<IList<AITool>> GetToolsAsync", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "GetToolsAsync should still exist under this name");
        var clientIsNullIndex = code.IndexOf("if (client is null)", start, StringComparison.Ordinal);
        clientIsNullIndex.Should().BeGreaterThan(start);
        var branchBody = code[clientIsNullIndex..(clientIsNullIndex + 800)];

        branchBody.Should().Contain("if (!cancellationToken.IsCancellationRequested)");
        branchBody.Should().Contain("SafeReportDiscoveryToBoundaryTracker");
    }

    [Fact]
    public async Task GetToolsAsync_ServerConfiguredButDisabled_ReturnsEmptyWithoutReportingToBoundaryTracker()
    {
        // #613: a disabled-but-configured server is fundamentally different information from "I tried
        // to connect and this server has zero tools" — the tool might be real the moment the server is
        // re-enabled. Reporting an empty discovery here would permanently fault (or, pre-#613, silently
        // trust) a plugin whose boundary references it. The fix must short-circuit BEFORE ever
        // attempting a connection, and skip the report entirely — leaving the entry genuinely pending,
        // not falsely resolved either way.
        var servers = new System.Collections.Concurrent.ConcurrentDictionary<string, Domain.Common.Config.AI.MCP.McpServerDefinition>
        {
            ["disabled-server"] = new() { Enabled = false },
        };
        var config = new Domain.Common.Config.AI.MCP.McpServersConfig { Servers = servers };
        var tracker = new Mock<IPluginToolBoundaryTracker>();
        var (sut, _) = CreateSut(tracker.Object, config);

        var tools = await sut.GetToolsAsync("disabled-server");

        tools.Should().BeEmpty();
        tracker.Verify(
            t => t.ReportServerToolsDiscovered(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>()),
            Times.Never);
    }

    [Fact]
    public void GetToolsAsync_SourceReportsEmptyDiscoveryWhenConnectionFails()
    {
        // The other, more common failure shape: the server can't even be CONNECTED to (genuinely
        // down, misconfigured). That never reaches DiscoverToolsAsync at all — GetToolsAsync's own
        // "client is null" branch is the only place that can report it, so a pending plugin boundary
        // entry doesn't wait forever for a connection that will never succeed.
        var path = RepoRoot.Combine(
            "src", "Content", "Infrastructure", "Infrastructure.AI.MCP", "Services", "McpToolProvider.cs");
        var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(path));

        var getToolsAsyncStart = code.IndexOf("public async Task<IList<AITool>> GetToolsAsync", StringComparison.Ordinal);
        getToolsAsyncStart.Should().BeGreaterThan(-1, "GetToolsAsync should still exist under this name");
        var methodBody = code[getToolsAsyncStart..(getToolsAsyncStart + 1500)];

        methodBody.Should().Contain("if (client is null)");
        methodBody.Should().Contain("ReportDiscoveryToBoundaryTracker(serverName, [])");
    }
}

using Application.AI.Common.Interfaces;
using Application.AI.Common.Services.Tools;
using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// Tests for the decorator that caches each MCP server's tool list within one ambient
/// <see cref="McpToolListCacheAccessor"/> scope (#495), so a caller who has already fetched every
/// connected server's tools does not pay for a second wire round trip when something later in the same
/// request re-resolves individual servers by name.
/// </summary>
public sealed class CachingMcpToolProviderTests
{
    private const string ServerA = "server-a";
    private const string ServerB = "server-b";

    private readonly Mock<IMcpToolProvider> _inner = new();
    private readonly CachingMcpToolProvider _sut;

    public CachingMcpToolProviderTests()
    {
        _sut = new CachingMcpToolProvider(_inner.Object);
    }

    [Fact]
    public async Task GetToolsAsync_NoActiveScope_AlwaysHitsTheInnerProvider()
    {
        // Off the one call site that opens a scope, this decorator must be invisible — a caching layer
        // that changed behaviour with no scope open would be a correctness regression for every other
        // consumer of IMcpToolProvider (ToolChainBuilder, BundleRunExecutor, ...).
        InnerReturns(ServerA, Tool("a1"));

        await _sut.GetToolsAsync(ServerA);
        await _sut.GetToolsAsync(ServerA);

        _inner.Verify(p => p.GetToolsAsync(ServerA, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetAllToolsAsync_PopulatesTheScope_SoALaterPerServerCallHitsTheCacheNotTheWire()
    {
        // The exact shape #495 is about: McpController's envelope-resolution fallback fetches every
        // server via GetAllToolsAsync, then DirectToolInvoker.ResolveGrantedMcpToolAsync re-fetches
        // individual servers one at a time to find the tool being invoked. The second call must not
        // reach the inner provider at all when it names a server the first call already discovered.
        _inner
            .Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>>
            {
                [ServerA] = [Tool("a1")],
                [ServerB] = [Tool("b1")],
            });

        using (McpToolListCacheAccessor.Begin())
        {
            await _sut.GetAllToolsAsync();

            var fromCache = await _sut.GetToolsAsync(ServerA);

            fromCache.Select(t => t.Name).Should().Equal("a1");
        }

        _inner.Verify(p => p.GetToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetToolsAsync_ASecondCallForTheSameServerInTheSameScope_HitsTheCache()
    {
        InnerReturns(ServerA, Tool("a1"));

        using (McpToolListCacheAccessor.Begin())
        {
            await _sut.GetToolsAsync(ServerA);
            await _sut.GetToolsAsync(ServerA);
        }

        _inner.Verify(p => p.GetToolsAsync(ServerA, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetToolsAsync_AServerNotYetInTheScope_StillFetchesFromTheInnerProvider_AndThenCachesIt()
    {
        // A cache miss must fall through, not fail closed — an operator-configured narrow grant that
        // never calls GetAllToolsAsync first must keep working exactly as it did before this existed.
        InnerReturns(ServerA, Tool("a1"));

        using (McpToolListCacheAccessor.Begin())
        {
            var first = await _sut.GetToolsAsync(ServerA);
            var second = await _sut.GetToolsAsync(ServerA);

            first.Select(t => t.Name).Should().Equal("a1");
            second.Should().BeSameAs(first);
        }

        _inner.Verify(p => p.GetToolsAsync(ServerA, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScopesDoNotLeakAcrossOneAnother_EachBeginStartsEmpty()
    {
        InnerReturns(ServerA, Tool("a1"));

        using (McpToolListCacheAccessor.Begin())
            await _sut.GetToolsAsync(ServerA);

        // A fresh scope must not inherit a server list a previous, already-disposed scope discovered —
        // a cache that outlived its request would go stale the moment the server's tools changed.
        using (McpToolListCacheAccessor.Begin())
            await _sut.GetToolsAsync(ServerA);

        _inner.Verify(p => p.GetToolsAsync(ServerA, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetAllToolsAsync_PopulatingOneServersEntry_NeverAnswersALookupForADifferentServer()
    {
        // Security review finding (#495): the grant-enforcement re-resolution
        // (DirectToolInvoker.ResolveGrantedMcpToolAsync) walks a caller's granted server list one name
        // at a time. This is the sharpest proof available at this layer that the cache cannot turn that
        // lookup into contact with a server the caller was never granted: populating the cache with
        // ServerA's tools must never satisfy a GetToolsAsync(ServerB) call for an ungranted server.
        _inner
            .Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>> { [ServerA] = [Tool("a1")] });
        InnerReturns(ServerB, Tool("b1"));

        using (McpToolListCacheAccessor.Begin())
        {
            await _sut.GetAllToolsAsync();

            await _sut.GetToolsAsync(ServerB);
        }

        _inner.Verify(p => p.GetToolsAsync(ServerB, It.IsAny<CancellationToken>()), Times.Once,
            "ServerB was never discovered, so asking for it must still reach the inner provider — a "
            + "cache miss must never resolve to another server's cached entry");
    }

    [Fact]
    public async Task ConcurrentFlows_EachWithItsOwnScope_DoNotSeeEachOthersCache()
    {
        // Pins the property Q1/Q4 of the security review depend on: AsyncLocal isolates concurrent
        // flows from each other, which is what makes this an ambient per-REQUEST cache rather than an
        // accidental per-process one. A future "optimisation" to a shared static field would break this
        // silently — this test is what catches it.
        InnerReturns(ServerA, Tool("a1"));
        InnerReturns(ServerB, Tool("b1"));

        var flowASawServerB = false;
        var flowBSawServerA = false;

        var flowA = Task.Run(async () =>
        {
            using (McpToolListCacheAccessor.Begin())
            {
                await _sut.GetToolsAsync(ServerA);
                await Task.Delay(20);
                flowASawServerB = McpToolListCacheAccessor.TryGet(ServerB, out _);
            }
        });
        var flowB = Task.Run(async () =>
        {
            using (McpToolListCacheAccessor.Begin())
            {
                await _sut.GetToolsAsync(ServerB);
                await Task.Delay(20);
                flowBSawServerA = McpToolListCacheAccessor.TryGet(ServerA, out _);
            }
        });

        await Task.WhenAll(flowA, flowB);

        flowASawServerB.Should().BeFalse();
        flowBSawServerA.Should().BeFalse();
    }

    [Fact]
    public async Task GetToolByNameAsync_IsNeverCached_AndAlwaysDelegates()
    {
        // Different shape from a per-server fetch — see the type's remarks on why this path is not
        // cached, matching BehaviorRecordingMcpToolProvider's own reasoning for the same method.
        _inner
            .Setup(p => p.GetToolByNameAsync("search", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Tool("search"));

        using (McpToolListCacheAccessor.Begin())
        {
            await _sut.GetToolByNameAsync("search");
            await _sut.GetToolByNameAsync("search");
        }

        _inner.Verify(p => p.GetToolByNameAsync("search", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetAllToolsAsync_ReturnsTheInnerProvidersResultUnchanged()
    {
        var discovered = new Dictionary<string, IList<AITool>>
        {
            [ServerA] = [Tool("a1"), Tool("a2")],
        };
        _inner.Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(discovered);

        var result = await _sut.GetAllToolsAsync();

        result.Should().BeSameAs(discovered);
    }

    private void InnerReturns(string serverName, params AITool[] tools) =>
        _inner
            .Setup(p => p.GetToolsAsync(serverName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tools.ToList());

    private static AIFunction Tool(string name) =>
        AIFunctionFactory.Create(() => "result", name, "does a thing");
}

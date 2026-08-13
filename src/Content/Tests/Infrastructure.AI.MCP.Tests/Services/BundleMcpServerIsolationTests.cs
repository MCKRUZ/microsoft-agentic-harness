using System.Collections.Concurrent;
using Application.AI.Common.Exceptions;
using Domain.Common.Config.AI.MCP;
using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// Regression tests for the #370 security fix: a bundle-owned MCP server definition must never be
/// reachable from <see cref="McpConnectionManager.GetConfiguredServerNames"/> — the enumeration
/// chokepoint every ordinary, non-bundle agent conversation (<c>McpToolProvider.GetAllToolsAsync</c>/
/// <c>GetToolByNameAsync</c>, and through them the MCP REST endpoints) reads with no bundle-provenance
/// filter of its own. Before this fix, a bundle's own server was written into the same
/// <see cref="McpServersConfig"/> the host enumerates, so uploading a malicious bundle — no run
/// required — let its tools reach every other conversation on the host under their bare, attacker-chosen
/// names. These tests exercise the fix at the layer it actually lives in: which of the two registries a
/// name is reachable from, not any downstream consumer's logic (which needed no changes).
/// </summary>
public sealed class BundleMcpServerIsolationTests
{
    private static McpConnectionManager CreateManager(McpServersConfig hostConfig, BundleOwnedMcpServerRegistry bundleOwned)
    {
        return McpConnectionManagerBundleEgressSupport.CreateManager(
            Mock.Of<ILogger<McpConnectionManager>>(),
            new Mock<ILoggerFactory>().Object,
            TestSsrf.HandlerFactory(),
            hostConfig,
            bundleOwned);
    }

    [Fact]
    public void BundleOwnedMcpServerRegistry_ExposesNoEnumerationSurface()
    {
        // Mechanical guard for the doc comment's own claim: "no enumeration API at all". A future
        // convenience method (a Keys property, a GetAll(bundleId) helper, implementing IEnumerable) would
        // silently reopen exactly the leak this whole fix exists to prevent, since ANY enumerable surface
        // on this type is one careless foreach away from becoming a second GetConfiguredServerNames-style
        // chokepoint. This test fails the build the moment that happens, rather than relying on a
        // reviewer noticing.
        var type = typeof(BundleOwnedMcpServerRegistry);

        type.Should().NotBeAssignableTo<System.Collections.IEnumerable>(
            "the registry must never be directly enumerable");

        var publicMemberNames = type
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToList();
        publicMemberNames.Should().BeEquivalentTo(
            [nameof(BundleOwnedMcpServerRegistry.TryAdd), nameof(BundleOwnedMcpServerRegistry.TryRemove), nameof(BundleOwnedMcpServerRegistry.TryGetValue)],
            "the only way to read this registry is an exact-name lookup — adding any other public member " +
            "(Keys, Values, Count, GetEnumerator, ...) needs a deliberate, reviewed decision, not an accident");
    }

    [Fact]
    public void GetConfiguredServerNames_NeverReturnsBundleOwnedServers()
    {
        // The assertion that encodes the fix: a bundle-owned server, even enabled, is invisible to the
        // one enumeration surface every ordinary conversation's tool discovery is built on.
        var hostConfig = new McpServersConfig();
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:evil", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Http,
            Url = "https://attacker.example/mcp"
        });
        var sut = CreateManager(hostConfig, bundleOwned);

        var names = sut.GetConfiguredServerNames().ToList();

        names.Should().BeEmpty("a bundle-owned server must never be enumerable, regardless of Enabled");
    }

    [Fact]
    public void GetConfiguredServerNames_ReturnsHostServers_WhenBundleRegistryAlsoPopulated()
    {
        // A populated bundle registry must not suppress or interfere with host enumeration — the two
        // are independent, not a filtered view of one merged set.
        var hostConfig = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["azure:filesystem"] = new() { Enabled = true }
            }
        };
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:evil", new McpServerDefinition { Enabled = true, Type = McpServerType.Http, Url = "https://attacker.example/mcp" });
        var sut = CreateManager(hostConfig, bundleOwned);

        var names = sut.GetConfiguredServerNames().ToList();

        names.Should().BeEquivalentTo(["azure:filesystem"]);
    }

    [Fact]
    public async Task GetClientAsync_ResolvesBundleOwnedServerByExactName_ButFailsAtConnectNotLookup()
    {
        // Required, not optional: a bundle run's own already-envelope-gated resolution (ToolChainBuilder)
        // must still reach its own server by exact name. There's no real MCP server behind this URL, so
        // the connect attempt fails -- the point is WHERE it fails: past the "is not configured" lookup
        // guard, proving the definition was found in the bundle registry.
        var hostConfig = new McpServersConfig();
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:echo", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Http,
            Url = "https://127.0.0.1.nip.io:9/mcp" // guaranteed connection refused, not a lookup miss
        });
        var sut = CreateManager(hostConfig, bundleOwned);

        var act = () => sut.GetClientAsync("b1:echo");

        var exception = await act.Should().ThrowAsync<McpConnectionException>();
        exception.Which.Message.Should().NotContain("is not configured",
            "the definition must be found in the bundle registry, not treated as unknown");
    }

    [Fact]
    public async Task GetClientAsync_PrefersHostDefinition_WhenNamePresentInBoth()
    {
        // Host-first is deliberate: the trusted source must win outright over the untrusted fallback.
        // Both definitions target an unreachable endpoint so this only needs to prove WHICH one was
        // selected, not that either successfully connects -- distinguished by StartupTimeoutSeconds
        // surfacing in the wrapped exception's inner detail is too fragile; instead assert via the
        // disabled-host-wins-as-"disabled" signal, which only the host definition carries.
        var hostConfig = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["shared-name"] = new() { Enabled = false } // host def: distinguishable by "disabled"
            }
        };
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("shared-name", new McpServerDefinition
        {
            Enabled = true, Type = McpServerType.Http, Url = "https://attacker.example/mcp"
        });
        var sut = CreateManager(hostConfig, bundleOwned);

        var act = () => sut.GetClientAsync("shared-name");

        var exception = await act.Should().ThrowAsync<McpConnectionException>();
        exception.Which.Message.Should().Contain("disabled",
            "the host definition (Enabled=false) must win over the bundle-owned one with the same name");
    }
}

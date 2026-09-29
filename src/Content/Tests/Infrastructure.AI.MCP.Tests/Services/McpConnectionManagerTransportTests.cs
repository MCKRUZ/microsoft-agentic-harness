using System.Collections.Concurrent;
using System.Reflection;
using Application.AI.Common.Exceptions;
using Domain.Common.Config.AI.MCP;
using Infrastructure.AI.Bundles;
using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// Tests for <see cref="McpConnectionManager"/> transport creation paths
/// covering HTTP auth header injection, SSE transport, and concurrent access.
/// </summary>
public sealed class McpConnectionManagerTransportTests
{
    private static McpConnectionManager CreateManager(
        McpServersConfig? config = null, BundleOwnedMcpServerRegistry? bundleOwned = null)
    {
        return McpConnectionManagerBundleEgressSupport.CreateManager(
            Mock.Of<ILogger<McpConnectionManager>>(),
            new Mock<ILoggerFactory>().Object,
            TestSsrf.HandlerFactory(),
            config ?? new McpServersConfig(),
            bundleOwned ?? new BundleOwnedMcpServerRegistry());
    }

    // -- SSE transport --

    [Fact]
    public async Task GetClientAsync_SseServerWithNoUrl_ThrowsMcpConnectionException()
    {
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["sse-test"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Sse,
                    Url = null,
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("sse-test");

        await act.Should().ThrowAsync<McpConnectionException>();
    }

    // -- HTTP with auth --

    [Fact]
    public async Task GetClientAsync_HttpWithApiKeyAuth_ThrowsOnConnection()
    {
        // The transport is created but connection to a fake URL will fail.
        // This tests that CreateHttpTransport doesn't throw during construction.
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["api-key-server"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Http,
                    Url = "http://localhost:19999/mcp",
                    StartupTimeoutSeconds = 1,
                    Auth = new McpServerAuthConfig
                    {
                        Type = McpServerAuthType.ApiKey,
                        ApiKey = "test-api-key",
                        ApiKeyHeader = "X-API-Key"
                    }
                }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("api-key-server");

        // Will throw McpConnectionException wrapping the actual transport failure
        await act.Should().ThrowAsync<McpConnectionException>();
    }

    [Fact]
    public async Task GetClientAsync_HttpWithBearerAuth_ThrowsOnConnection()
    {
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["bearer-server"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Http,
                    Url = "http://localhost:19999/mcp",
                    StartupTimeoutSeconds = 1,
                    Auth = new McpServerAuthConfig
                    {
                        Type = McpServerAuthType.Bearer,
                        BearerToken = "test-bearer-token"
                    }
                }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("bearer-server");

        await act.Should().ThrowAsync<McpConnectionException>();
    }

    [Fact]
    public async Task GetClientAsync_HttpWithIncompleteEntraAuth_ThrowsWithClearMessage()
    {
        // Entra type selected but no scope — previously this silently connected with no
        // credential. It must now fail loudly at transport build before any send.
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["entra-no-scope"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Http,
                    Url = "http://localhost:19999/mcp",
                    StartupTimeoutSeconds = 1,
                    Auth = new McpServerAuthConfig
                    {
                        Type = McpServerAuthType.Entra
                    }
                }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("entra-no-scope");

        await act.Should().ThrowAsync<McpConnectionException>()
            .WithMessage("*incomplete*");
    }

    [Fact]
    public async Task GetClientAsync_HttpWithIncompleteBearerAuth_ThrowsWithClearMessage()
    {
        // A configured-but-empty static credential must also fail loudly rather than
        // connecting with no Authorization header.
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["bearer-empty"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Http,
                    Url = "http://localhost:19999/mcp",
                    StartupTimeoutSeconds = 1,
                    Auth = new McpServerAuthConfig
                    {
                        Type = McpServerAuthType.Bearer,
                        BearerToken = ""
                    }
                }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("bearer-empty");

        await act.Should().ThrowAsync<McpConnectionException>()
            .WithMessage("*incomplete*");
    }

    [Fact]
    public async Task GetClientAsync_EntraServer_CachesPerServerClient_DisconnectRemovesIt()
    {
        // Drives the Entra happy-path branch at the manager level: a valid managed-identity
        // Entra config builds and caches a per-server token-injecting client (this happens
        // during transport construction, before any token call), and DisconnectAsync must
        // release it. The connection itself fails (unreachable URL / SSRF) — we assert the
        // lifecycle of the cached client, not a successful connect.
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["entra-server"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Http,
                    Url = "http://localhost:19999/mcp",
                    StartupTimeoutSeconds = 1,
                    Auth = new McpServerAuthConfig
                    {
                        Type = McpServerAuthType.Entra,
                        Scopes = ["api://resource/.default"]
                    }
                }
            }
        };
        var sut = CreateManager(config);
        var entraClients = GetEntraClients(sut);

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            var act = () => sut.GetClientAsync("entra-server", cts.Token);
            await act.Should().ThrowAsync<McpConnectionException>();
        }

        // The per-server Entra client was created and cached even though the connect failed.
        entraClients.Should().ContainKey("entra-server");

        await sut.DisconnectAsync("entra-server");

        // DisconnectAsync released it — the defended cleanup behavior, now guarded.
        entraClients.Should().NotContainKey("entra-server");
    }

    private static ConcurrentDictionary<string, HttpClient> GetEntraClients(McpConnectionManager manager)
    {
        var field = typeof(McpConnectionManager)
            .GetField("_entraClients", BindingFlags.NonPublic | BindingFlags.Instance);
        return (ConcurrentDictionary<string, HttpClient>)field!.GetValue(manager)!;
    }

    // -- Session-sharing security invariant (no live server needed; this is a shape guard, not a
    // behavioral one — see the remarks on _clients for what it protects) --

    /// <summary>
    /// Pins the current, intentional key TYPE of every cache that holds a live, auth-bearing MCP
    /// session or client: a bare <see cref="string"/> (server name), not a composite that could carry
    /// a caller/user/tenant dimension. This is safe today only because no per-caller credential is
    /// ever attached to a cached session (see the SECURITY INVARIANT remarks on
    /// <c>McpConnectionManager._clients</c>).
    /// </summary>
    /// <remarks>
    /// This is a type-shape guard, not a content guard — it catches the shape of change this
    /// codebase's own <c>_runScopedClients</c> already demonstrates (widening a cache's key from
    /// <see cref="string"/> to a tuple), which is the most natural way an engineer following existing
    /// precedent in this file would add per-caller scoping. It does <b>not</b> catch a caller identity
    /// concatenated into an otherwise-unchanged <see cref="string"/> key, and it does not catch caller
    /// scoping introduced via an ambient accessor — this file's own <c>BundleRunIdAccessor</c> pattern
    /// (an AsyncLocal read inside <c>RequiresRunScope</c>) is a working example of exactly that shape,
    /// and a caller-identity equivalent would leave every cache's declared key type untouched while
    /// still changing what gets shared. Neither of those is a defect this reflection-based test can
    /// close: catching them needs a live-server behavioral test, which the rest of this file avoids by
    /// design (every existing case here asserts on connection failure, not a successful session). A
    /// human review of any change to <c>ResolveHostConfiguredTransportHttpClient</c>,
    /// <c>EntraTokenAuthHandler.Create</c>, or the cache-key construction sites remains required if
    /// per-caller MCP credentials are ever introduced — see the tracked gap Microsoft's Agent Framework
    /// closed upstream for its own provider-backed MCP sessions (PR #8425, "Scope provider-backed MCP
    /// sessions per invocation").
    /// </remarks>
    [Fact]
    public void SharedCaches_AreKeyedByServerNameOnly()
    {
        AssertGenericDictionaryKeyType("_clients").Should().Be(typeof(string),
            "_clients must stay keyed by bare server name — widening its key TYPE to a composite is one " +
            "way this could be caught (this test exists for that case). A caller identity concatenated " +
            "into the existing string key, or introduced via an ambient accessor mirroring " +
            "BundleRunIdAccessor, would NOT be caught here and needs human review of every credential " +
            "source that feeds a cached session.");
        AssertGenericDictionaryKeyType("_entraClients").Should().Be(typeof(string),
            "_entraClients must stay keyed by bare server name — see the _clients assertion above for why");
        AssertGenericDictionaryKeyType("_bundleEgressClients").Should().Be(typeof(string),
            "_bundleEgressClients must stay keyed by bare server name — see the _clients assertion above for why");

        // _runScopedClients is deliberately NOT server-name-only: it is keyed by (ServerName, RunId)
        // because the stdio transport is single-session and must never be shared across concurrent
        // runs. That is a stronger isolation guarantee than the invariant this test protects, not a
        // violation of it — pinned here so a reader doesn't mistake its different shape for a gap.
        AssertGenericDictionaryKeyType("_runScopedClients").Should().Be(typeof((string ServerName, string RunId)));

        static Type AssertGenericDictionaryKeyType(string fieldName)
        {
            var field = typeof(McpConnectionManager)
                .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            field.Should().NotBeNull(
                $"{fieldName} is expected to exist on McpConnectionManager as a shared session/client cache");

            var genericArgs = field!.FieldType.GetGenericArguments();
            genericArgs.Should().NotBeEmpty(
                $"{fieldName} is expected to stay a generic dictionary — if it ever becomes a non-generic " +
                "type, that needs the same conscious review as changing its key type outright.");

            return genericArgs[0];
        }
    }

    // -- Concurrent GetClientAsync --

    [Fact]
    public async Task GetClientAsync_ConcurrentCallsSameServer_ThrowsSameException()
    {
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["concurrent-test"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Stdio,
                    Command = "",
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var sut = CreateManager(config);

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => Assert.ThrowsAsync<McpConnectionException>(
                () => sut.GetClientAsync("concurrent-test")));

        var exceptions = await Task.WhenAll(tasks);

        exceptions.Should().AllBeOfType<McpConnectionException>();
    }

    // -- Unsupported transport type --

    [Fact]
    public async Task GetClientAsync_UnsupportedType_ThrowsMcpConnectionException()
    {
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["bad-type"] = new()
                {
                    Enabled = true,
                    Type = (McpServerType)999,
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("bad-type");

        await act.Should().ThrowAsync<McpConnectionException>()
            .WithMessage("*Unsupported*");
    }

    // -- Stdio with environment variables --

    [Fact]
    public async Task GetClientAsync_StdioWithEnvVars_ThrowsMcpConnectionException()
    {
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["env-test"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Stdio,
                    Command = "nonexistent-binary",
                    StartupTimeoutSeconds = 1,
                    Env = new Dictionary<string, string>
                    {
                        ["TEST_VAR"] = "test-value"
                    }
                }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("env-test");

        await act.Should().ThrowAsync<McpConnectionException>();
    }
}

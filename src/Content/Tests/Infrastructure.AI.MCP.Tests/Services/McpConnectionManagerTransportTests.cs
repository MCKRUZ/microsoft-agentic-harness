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
    /// Pins the current, intentional key shape of every cache that holds a live, auth-bearing MCP
    /// session or client: keyed by server name alone, with no caller/user/tenant dimension. This is
    /// safe today only because no per-caller credential is ever attached to a cached session (see the
    /// SECURITY INVARIANT remarks on <c>McpConnectionManager._clients</c>). If a future change ever
    /// widens any of these keys to admit per-caller identity — exactly the fix Microsoft's Agent
    /// Framework shipped upstream in PR #8425 for its own provider-backed MCP sessions — that change
    /// must be a deliberate, reviewed decision, not an accidental side effect of an unrelated refactor.
    /// This test exists to force that decision to be conscious: it fails the moment any of these three
    /// caches stops being keyed by bare <see cref="string"/> server name.
    /// </summary>
    [Fact]
    public void SharedCaches_AreKeyedByServerNameOnly()
    {
        AssertKeyedByServerNameOnly("_clients");
        AssertKeyedByServerNameOnly("_entraClients");
        AssertKeyedByServerNameOnly("_bundleEgressClients");

        // _runScopedClients is deliberately NOT server-name-only: it is keyed by (ServerName, RunId)
        // because the stdio transport is single-session and must never be shared across concurrent
        // runs. That is a stronger isolation guarantee than the invariant this test protects, not a
        // violation of it — pinned here so a reader doesn't mistake its different shape for a gap.
        var runScopedField = typeof(McpConnectionManager)
            .GetField("_runScopedClients", BindingFlags.NonPublic | BindingFlags.Instance);
        var runScopedKeyType = runScopedField!.FieldType.GetGenericArguments()[0];
        runScopedKeyType.Should().Be(typeof((string ServerName, string RunId)));

        static void AssertKeyedByServerNameOnly(string fieldName)
        {
            var field = typeof(McpConnectionManager)
                .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            field.Should().NotBeNull(
                $"{fieldName} is expected to exist on McpConnectionManager as a shared session/client cache");

            var keyType = field!.FieldType.GetGenericArguments()[0];
            keyType.Should().Be(typeof(string),
                $"{fieldName} must stay keyed by bare server name — widening its key to include caller " +
                "identity is exactly the class of change this test exists to catch. If that change is " +
                "intentional, update this test AND confirm every credential source that feeds a cached " +
                "session is genuinely per-caller from here on, not still sourced from static config.");
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

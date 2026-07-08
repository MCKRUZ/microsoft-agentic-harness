using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Infrastructure.AI.MCPServer.Tests.Hosting;

/// <summary>
/// Full-host integration tests proving the MCP server is fail-closed: it refuses to
/// start when no authentication is configured, enforces ApiKey / Bearer / Entra
/// credentials on the MCP endpoints, and serves anonymously only behind the explicit
/// <c>AppConfig:AI:MCP:Auth:AllowAnonymous</c> opt-in (with a prominent startup warning).
/// </summary>
public sealed class McpServerAuthFailClosedTests
{
    private const string TestApiKey = "test-api-key-not-a-real-secret";
    private const string TestBearerToken = "test-bearer-token-not-a-real-secret";

    // -- Startup fail-closed --

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Start_NoAuthConfigured_AllowAnonymousFalse_RefusesToStart(string environment)
    {
        // Environment name alone must never open the server: with Type=None and no
        // explicit AllowAnonymous opt-in, the host must fail at startup in EVERY
        // environment — including Development.
        var act = () => CreateFactory(new Dictionary<string, string?>
        {
            ["AppConfig:AI:MCP:Auth:Type"] = "None"
        }, environment);

        act.Should().Throw<Exception>()
            .Which.Should().Match<Exception>(e =>
                FlattenMessages(e).Contains("authentication is not configured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Start_ApiKeyTypeWithoutKeyMaterial_RefusesToStart()
    {
        // Type=ApiKey with no key is a misconfiguration — it must fail loudly at
        // startup, never boot un-enforced.
        var act = () => CreateFactory(new Dictionary<string, string?>
        {
            ["AppConfig:AI:MCP:Auth:Type"] = "ApiKey"
        });

        act.Should().Throw<Exception>()
            .Which.Should().Match<Exception>(e =>
                FlattenMessages(e).Contains("missing required credential material", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Start_EntraTypeMissingTenant_RefusesToStart()
    {
        var act = () => CreateFactory(new Dictionary<string, string?>
        {
            ["AppConfig:AI:MCP:Auth:Type"] = "Entra",
            ["AppConfig:AI:MCP:Auth:ClientId"] = "test-client-id"
        });

        act.Should().Throw<Exception>()
            .Which.Should().Match<Exception>(e =>
                FlattenMessages(e).Contains("missing required credential material", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Start_AllowAnonymousTrue_WithAuthConfigured_RefusesToStart()
    {
        // Contradictory config: an explicit anonymous opt-in combined with configured
        // credentials is ambiguous — fail loudly instead of guessing.
        var act = () => CreateFactory(new Dictionary<string, string?>
        {
            ["AppConfig:AI:MCP:Auth:Type"] = "ApiKey",
            ["AppConfig:AI:MCP:Auth:ApiKey"] = TestApiKey,
            ["AppConfig:AI:MCP:Auth:AllowAnonymous"] = "true"
        });

        act.Should().Throw<Exception>()
            .Which.Should().Match<Exception>(e =>
                FlattenMessages(e).Contains("contradictory", StringComparison.OrdinalIgnoreCase));
    }

    // -- ApiKey enforcement --

    [Fact]
    public async Task Request_ApiKeyConfigured_NoCredential_Returns401()
    {
        using var factory = CreateApiKeyFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateInitializeRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Request_ApiKeyConfigured_WrongKey_Returns401()
    {
        using var factory = CreateApiKeyFactory();
        using var client = factory.CreateClient();

        var request = CreateInitializeRequest();
        request.Headers.Add("X-API-Key", "wrong-key");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Request_ApiKeyConfigured_CorrectKey_IsServed()
    {
        using var factory = CreateApiKeyFactory();
        using var client = factory.CreateClient();

        var request = CreateInitializeRequest();
        request.Headers.Add("X-API-Key", TestApiKey);

        using var response = await client.SendAsync(request);

        response.IsSuccessStatusCode.Should().BeTrue(
            $"a correctly authenticated MCP initialize must be served (got {(int)response.StatusCode})");
    }

    // -- Bearer (static shared token) enforcement --

    [Fact]
    public async Task Request_BearerConfigured_NoCredential_Returns401()
    {
        using var factory = CreateBearerFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateInitializeRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Request_BearerConfigured_WrongToken_Returns401()
    {
        using var factory = CreateBearerFactory();
        using var client = factory.CreateClient();

        var request = CreateInitializeRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Request_BearerConfigured_CorrectToken_IsServed()
    {
        using var factory = CreateBearerFactory();
        using var client = factory.CreateClient();

        var request = CreateInitializeRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestBearerToken);

        using var response = await client.SendAsync(request);

        response.IsSuccessStatusCode.Should().BeTrue(
            $"a correctly authenticated MCP initialize must be served (got {(int)response.StatusCode})");
    }

    // RFC 7235: the auth-scheme token is case-insensitive and standards-compliant
    // clients may send extra whitespace between scheme and credential.

    [Theory]
    [InlineData("bearer")]
    [InlineData("BEARER")]
    [InlineData("BeArEr")]
    public async Task Request_BearerConfigured_SchemeCaseInsensitive_IsServed(string scheme)
    {
        using var factory = CreateBearerFactory();
        using var client = factory.CreateClient();

        var request = CreateInitializeRequest();
        request.Headers.TryAddWithoutValidation("Authorization", $"{scheme} {TestBearerToken}")
            .Should().BeTrue();

        using var response = await client.SendAsync(request);

        response.IsSuccessStatusCode.Should().BeTrue(
            $"RFC 7235 auth-scheme names are case-insensitive (got {(int)response.StatusCode})");
    }

    [Fact]
    public async Task Request_BearerConfigured_ExtraWhitespaceAfterScheme_IsServed()
    {
        using var factory = CreateBearerFactory();
        using var client = factory.CreateClient();

        var request = CreateInitializeRequest();
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer   {TestBearerToken}")
            .Should().BeTrue();

        using var response = await client.SendAsync(request);

        response.IsSuccessStatusCode.Should().BeTrue(
            $"whitespace between scheme and credential is tolerated (got {(int)response.StatusCode})");
    }

    [Fact]
    public async Task Request_BearerConfigured_WrongTokenUnderLowercaseScheme_Returns401()
    {
        using var factory = CreateBearerFactory();
        using var client = factory.CreateClient();

        var request = CreateInitializeRequest();
        request.Headers.TryAddWithoutValidation("Authorization", "bearer wrong-token")
            .Should().BeTrue();

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -- Entra (JWT) enforcement --

    [Fact]
    public async Task Request_EntraConfigured_NoToken_Returns401()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["AppConfig:AI:MCP:Auth:Type"] = "Entra",
            ["AppConfig:AI:MCP:Auth:TenantId"] = "00000000-0000-0000-0000-000000000001",
            ["AppConfig:AI:MCP:Auth:ClientId"] = "00000000-0000-0000-0000-000000000002"
        });
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateInitializeRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -- Explicit dev anonymous opt-in --

    [Fact]
    public async Task Request_AllowAnonymousTrue_IsServedAnonymously_AndWarningLogged()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["AppConfig:AI:MCP:Auth:Type"] = "None",
            ["AppConfig:AI:MCP:Auth:AllowAnonymous"] = "true"
        }, loggerProvider: logs);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateInitializeRequest());

        response.IsSuccessStatusCode.Should().BeTrue(
            $"AllowAnonymous=true is the explicit opt-in for anonymous serving (got {(int)response.StatusCode})");
        logs.Messages.Should().Contain(
            m => m.StartsWith("Warning:", StringComparison.Ordinal)
                 && m.Contains("ANONYMOUS", StringComparison.OrdinalIgnoreCase),
            "disabling authentication must log a prominent startup warning");
    }

    // -- Helpers --

    /// <summary>
    /// Serializes environment-variable overrides across factory startups: xUnit runs
    /// tests within this class sequentially, but the lock also guards against any
    /// future parallel host-booting test class.
    /// </summary>
    private static readonly object EnvironmentLock = new();

    /// <summary>
    /// Boots the full MCP server host with the given config overrides applied as
    /// environment variables. Env vars are the only default configuration source that
    /// both outranks appsettings.json and is visible to Program.Main's eager
    /// <c>builder.Configuration.GetSection("AppConfig").Get&lt;AppConfig&gt;()</c> read —
    /// WebApplicationFactory's ConfigureAppConfiguration overrides apply too late for it.
    /// Startup is forced inside the override scope; startup failures propagate to the caller.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(
        Dictionary<string, string?> settings,
        string environment = "Development",
        ILoggerProvider? loggerProvider = null)
    {
        lock (EnvironmentLock)
        {
            var variables = settings.ToDictionary(
                pair => pair.Key.Replace(":", "__", StringComparison.Ordinal),
                pair => pair.Value);
            foreach (var (key, value) in variables)
                Environment.SetEnvironmentVariable(key, value);

            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                if (loggerProvider is not null)
                    builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
            });

            try
            {
                _ = factory.Server; // Force host startup while the overrides are visible.
                return factory;
            }
            catch
            {
                factory.Dispose();
                throw;
            }
            finally
            {
                foreach (var key in variables.Keys)
                    Environment.SetEnvironmentVariable(key, null);
            }
        }
    }

    private static WebApplicationFactory<Program> CreateApiKeyFactory() =>
        CreateFactory(new Dictionary<string, string?>
        {
            ["AppConfig:AI:MCP:Auth:Type"] = "ApiKey",
            ["AppConfig:AI:MCP:Auth:ApiKey"] = TestApiKey
        });

    private static WebApplicationFactory<Program> CreateBearerFactory() =>
        CreateFactory(new Dictionary<string, string?>
        {
            ["AppConfig:AI:MCP:Auth:Type"] = "Bearer",
            ["AppConfig:AI:MCP:Auth:BearerToken"] = TestBearerToken
        });

    /// <summary>
    /// Builds an MCP <c>initialize</c> JSON-RPC request against the streamable HTTP
    /// endpoint — the entry point every MCP client must call first, and therefore the
    /// canonical probe for whether the server serves protocol traffic.
    /// </summary>
    private static HttpRequestMessage CreateInitializeRequest()
    {
        const string body = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"auth-tests","version":"1.0"}}}
            """;
        var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    private static string FlattenMessages(Exception exception)
    {
        var builder = new StringBuilder();
        for (Exception? current = exception; current is not null; current = current.InnerException)
            builder.Append(current.Message).Append(" | ");
        if (exception is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions)
                builder.Append(FlattenMessages(inner));
        return builder.ToString();
    }

    /// <summary>Captures log output so tests can assert on startup warnings.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        /// <summary>Formatted log lines as "{Level}:{Category}:{Message}".</summary>
        public ConcurrentBag<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages, categoryName);

        public void Dispose()
        {
            // Nothing to release — messages are kept for assertion after the host stops.
        }

        private sealed class CapturingLogger(ConcurrentBag<string> messages, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Add($"{logLevel}:{category}:{formatter(state, exception)}");
        }
    }
}

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Infrastructure.AI.MCPServer.Tests.Hosting;

/// <summary>
/// Full-host integration tests pinning what an MCP client sees over HTTP on each protocol revision
/// the server supports: a <c>2026-07-28</c> client calls a tool with no <c>initialize</c> handshake and
/// no session, a <c>2025-11-25</c> client still completes the handshake and lists tools, and neither
/// is told the server supports resource subscriptions. Resources stay listable, subscribe and
/// unsubscribe fail with method-not-found, and the deprecated <c>logging/setLevel</c> is refused on
/// <c>2026-07-28</c>.
/// </summary>
/// <remarks>
/// Requests are raw JSON-RPC posts rather than the SDK client, so each test controls exactly which
/// revision's wire format it sends and asserts only on status codes, headers and response bodies.
/// The rate limiter is one 100-per-minute window per host; every test sends at most three requests.
/// The <c>logging</c> capability itself stays advertised: ModelContextProtocol 2.2.0 always sets it,
/// and the <c>2026-07-28</c> revision deprecates the capability rather than removing it.
/// </remarks>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class McpServerProtocolVersionTests
{
    private const string TestApiKey = "test-api-key-not-a-real-secret";
    private const string CurrentRevision = "2026-07-28";
    private const string HandshakeRevision = "2025-11-25";
    private const string SessionIdHeader = "Mcp-Session-Id";
    private const int MethodNotFound = -32601;

    // -- 2026-07-28: no handshake, no session --

    [Theory]
    [InlineData(AuthMode.Anonymous)]
    [InlineData(AuthMode.ApiKey)]
    public async Task ToolsCall_On20260728WithoutHandshake_ReturnsToolResult(AuthMode authMode)
    {
        using var factory = CreateFactory(authMode);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateCurrentRevisionRequest(
            authMode, "tools/call",
            new JsonObject { ["name"] = "list_skills", ["arguments"] = new JsonObject() }));
        var message = await ReadJsonRpcMessageAsync(response);

        var result = message["result"];
        result.Should().NotBeNull($"a {CurrentRevision} tool call needs no handshake (got {message.ToJsonString()})");
        result!["isError"]?.GetValue<bool>().Should().NotBe(true, "list_skills must run, not fail");
    }

    [Fact]
    public async Task ToolsCall_On20260728WithoutHandshake_DoesNotMintSessionId()
    {
        using var factory = CreateFactory(AuthMode.Anonymous);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateCurrentRevisionRequest(
            AuthMode.Anonymous, "tools/call",
            new JsonObject { ["name"] = "list_skills", ["arguments"] = new JsonObject() }));

        response.Headers.Contains(SessionIdHeader).Should().BeFalse(
            $"the {CurrentRevision} revision removed sessions; the server must not mint one");
    }

    // -- 2025-11-25: initialize handshake --

    [Fact]
    public async Task ToolsList_On20251125AfterHandshake_ReturnsSkillTools()
    {
        using var factory = CreateFactory(AuthMode.Anonymous);
        using var client = factory.CreateClient();
        var sessionId = await CompleteHandshakeAsync(client);

        using var response = await client.SendAsync(CreateHandshakeRevisionRequest(
            sessionId, id: 2, "tools/list", new JsonObject()));
        var message = await ReadJsonRpcMessageAsync(response);

        var toolNames = message["result"]?["tools"]?.AsArray()
            .Select(tool => tool?["name"]?.GetValue<string>())
            .ToList();
        toolNames.Should().Contain(["list_skills", "get_skill", "find_skills_by_tag"],
            $"a {HandshakeRevision} client must still list the built-in skill tools (got {message.ToJsonString()})");
    }

    [Theory]
    [InlineData("resources/subscribe")]
    [InlineData("resources/unsubscribe")]
    public async Task ResourceSubscriptionMethod_On20251125AfterHandshake_ReturnsMethodNotFound(string method)
    {
        using var factory = CreateFactory(AuthMode.Anonymous);
        using var client = factory.CreateClient();
        var sessionId = await CompleteHandshakeAsync(client);

        using var response = await client.SendAsync(CreateHandshakeRevisionRequest(
            sessionId, id: 2, method, new JsonObject { ["uri"] = "harness://anything" }));
        var message = await ReadJsonRpcMessageAsync(response);

        int? errorCode = message["error"]?["code"]?.GetValue<int>();
        errorCode.Should().Be(MethodNotFound,
            $"nothing ever sends resource updates, so {method} must fail fast (got {message.ToJsonString()})");
    }

    [Fact]
    public async Task ResourcesList_On20251125AfterHandshake_ReturnsEmptyList()
    {
        using var factory = CreateFactory(AuthMode.Anonymous);
        using var client = factory.CreateClient();
        var sessionId = await CompleteHandshakeAsync(client);

        using var response = await client.SendAsync(CreateHandshakeRevisionRequest(
            sessionId, id: 2, "resources/list", new JsonObject()));
        var message = await ReadJsonRpcMessageAsync(response);

        var resources = message["result"]?["resources"];
        resources.Should().NotBeNull($"resources/list must still be served (got {message.ToJsonString()})");
        resources!.AsArray().Should().BeEmpty(
            $"no resource assembly is configured, so the list is empty (got {message.ToJsonString()})");
    }

    // -- 2026-07-28: deprecated logging/setLevel --

    [Fact]
    public async Task LoggingSetLevel_On20260728_ReturnsMethodNotFound()
    {
        using var factory = CreateFactory(AuthMode.Anonymous);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateCurrentRevisionRequest(
            AuthMode.Anonymous, "logging/setLevel", new JsonObject { ["level"] = "info" }));
        // The SDK refuses removed methods before dispatch, with an HTTP error status around the
        // JSON-RPC error body; the body is what a client acts on, so the status is not pinned.
        var message = await ReadJsonRpcMessageAsync(response, requireSuccessStatus: false);

        int? errorCode = message["error"]?["code"]?.GetValue<int>();
        errorCode.Should().Be(MethodNotFound,
            $"{CurrentRevision} removed logging/setLevel; log verbosity is server config (got {message.ToJsonString()})");
    }

    // -- Advertised capabilities --

    [Fact]
    public async Task Initialize_On20251125_DoesNotAdvertiseResourceSubscriptions()
    {
        using var factory = CreateFactory(AuthMode.Anonymous);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateInitializeRequest());
        var message = await ReadJsonRpcMessageAsync(response);

        AssertNoResourceSubscriptions(message["result"]?["capabilities"], message);
    }

    [Fact]
    public async Task ServerDiscover_On20260728_DoesNotAdvertiseResourceSubscriptions()
    {
        using var factory = CreateFactory(AuthMode.Anonymous);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateCurrentRevisionRequest(
            AuthMode.Anonymous, "server/discover", new JsonObject()));
        var message = await ReadJsonRpcMessageAsync(response);

        AssertNoResourceSubscriptions(message["result"]?["capabilities"], message);
    }

    // -- Helpers --

    /// <summary>How the booted host authenticates; set through test config only, never auth code.</summary>
    public enum AuthMode
    {
        /// <summary>The explicit <c>AllowAnonymous</c> development opt-in.</summary>
        Anonymous,

        /// <summary>The shared API key on <c>X-API-Key</c>.</summary>
        ApiKey,
    }

    private static void AssertNoResourceSubscriptions(JsonNode? capabilities, JsonNode message)
    {
        capabilities.Should().NotBeNull($"the response must carry a capabilities object (got {message.ToJsonString()})");
        capabilities!["resources"].Should().NotBeNull(
            $"resources/list and resources/read are still served (got {message.ToJsonString()})");
        capabilities["resources"]!["subscribe"].Should().BeNull(
            $"nothing ever sends notifications/resources/updated (got {message.ToJsonString()})");
    }

    /// <summary>
    /// Runs the <c>2025-11-25</c> handshake (<c>initialize</c> then <c>notifications/initialized</c>)
    /// and returns the session id the server minted, or <see langword="null"/> when it runs stateless.
    /// </summary>
    private static async Task<string?> CompleteHandshakeAsync(HttpClient client)
    {
        using var initialize = await client.SendAsync(CreateInitializeRequest());
        var message = await ReadJsonRpcMessageAsync(initialize);
        message["result"].Should().NotBeNull($"initialize must succeed (got {message.ToJsonString()})");
        var sessionId = initialize.Headers.TryGetValues(SessionIdHeader, out var values)
            ? values.Single()
            : null;

        using var initialized = await client.SendAsync(CreateHandshakeRevisionRequest(
            sessionId, id: null, "notifications/initialized", null));
        initialized.IsSuccessStatusCode.Should().BeTrue(
            $"notifications/initialized must be accepted (got {(int)initialized.StatusCode})");
        return sessionId;
    }

    private static HttpRequestMessage CreateInitializeRequest() =>
        CreateHandshakeRevisionRequest(sessionId: null, id: 1, "initialize", new JsonObject
        {
            ["protocolVersion"] = HandshakeRevision,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "protocol-tests", ["version"] = "1.0" },
        });

    private static HttpRequestMessage CreateHandshakeRevisionRequest(
        string? sessionId, int? id, string method, JsonObject? parameters)
    {
        var request = CreatePost(BuildEnvelope(id, method, parameters));
        if (method != "initialize")
            request.Headers.Add("MCP-Protocol-Version", HandshakeRevision);
        if (sessionId is not null)
            request.Headers.Add(SessionIdHeader, sessionId);
        return request;
    }

    /// <summary>
    /// Builds a <c>2026-07-28</c> request: the revision in both the <c>MCP-Protocol-Version</c> header
    /// and <c>params._meta</c>, the client's identity and capabilities in <c>_meta</c> on every request
    /// (SEP-2575), and the standard <c>Mcp-Method</c> header, plus <c>Mcp-Name</c> taken from
    /// <c>params.name</c> when the request names a tool.
    /// </summary>
    private static HttpRequestMessage CreateCurrentRevisionRequest(
        AuthMode authMode, string method, JsonObject parameters)
    {
        parameters["_meta"] = new JsonObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = CurrentRevision,
            ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "protocol-tests", ["version"] = "1.0" },
            ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
        };
        var request = CreatePost(BuildEnvelope(1, method, parameters));
        request.Headers.Add("MCP-Protocol-Version", CurrentRevision);
        request.Headers.Add("Mcp-Method", method);
        if (parameters["name"]?.GetValue<string>() is { } name)
            request.Headers.Add("Mcp-Name", name);
        if (authMode == AuthMode.ApiKey)
            request.Headers.Add("X-API-Key", TestApiKey);
        return request;
    }

    private static JsonObject BuildEnvelope(int? id, string method, JsonObject? parameters)
    {
        var envelope = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is not null)
            envelope["id"] = id.Value;
        if (parameters is not null)
            envelope["params"] = parameters;
        return envelope;
    }

    private static HttpRequestMessage CreatePost(JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    /// <summary>
    /// Reads the single JSON-RPC response from either a plain JSON body or a server-sent-event
    /// stream (the last <c>data:</c> line carrying a JSON-RPC message with an id). Unless
    /// <paramref name="requireSuccessStatus"/> is <see langword="false"/>, the HTTP status must be 2xx.
    /// </summary>
    private static async Task<JsonNode> ReadJsonRpcMessageAsync(
        HttpResponseMessage response, bool requireSuccessStatus = true)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (requireSuccessStatus)
            response.IsSuccessStatusCode.Should().BeTrue($"the MCP endpoint must serve the request (got {(int)response.StatusCode}: {body})");

        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            return JsonNode.Parse(body)!;

        var message = body.Split('\n')
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => JsonNode.Parse(line["data:".Length..].Trim()))
            .LastOrDefault(node => node?["id"] is not null);
        message.Should().NotBeNull($"the event stream must carry a JSON-RPC response (got {body})");
        return message!;
    }

    private static WebApplicationFactory<Program> CreateFactory(AuthMode authMode) =>
        CreateFactory(authMode == AuthMode.ApiKey
            ? new Dictionary<string, string?>
            {
                ["AppConfig:AI:MCP:Auth:Type"] = "ApiKey",
                ["AppConfig:AI:MCP:Auth:ApiKey"] = TestApiKey,
            }
            : new Dictionary<string, string?>
            {
                ["AppConfig:AI:MCP:Auth:Type"] = "None",
                ["AppConfig:AI:MCP:Auth:AllowAnonymous"] = "true",
            });

    /// <summary>
    /// Boots the full MCP server host in Development with the given overrides applied as environment
    /// variables — the only default source both outranking appsettings.json and visible to
    /// <c>Program.Main</c>'s eager <c>AppConfig</c> read. <see cref="ProcessEnvironmentCollection"/>
    /// keeps any other host-booting class from running meanwhile.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?> settings)
    {
        var variables = settings.ToDictionary(
            pair => pair.Key.Replace(":", "__", StringComparison.Ordinal),
            pair => pair.Value);
        foreach (var (key, value) in variables)
            Environment.SetEnvironmentVariable(key, value);

        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
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

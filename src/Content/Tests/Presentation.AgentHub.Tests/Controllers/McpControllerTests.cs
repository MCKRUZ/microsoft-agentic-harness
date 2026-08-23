using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Bundles;
using Domain.AI.Governance;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;
using Presentation.AgentHub.DTOs;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Integration tests for <see cref="Presentation.AgentHub.Controllers.McpController"/> HTTP endpoints.
/// Covers tool listing, tool invocation, error handling, audit logging,
/// and the 32 KB request size limit.
/// </summary>
public sealed class McpControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    /// <summary>Initialises the test class with the shared factory.</summary>
    public McpControllerTests(TestWebApplicationFactory factory) => _factory = factory;

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The fake tool provider's one server key. #481 routes invocation through
    /// <c>IDirectToolInvoker.InvokeMcpToolAsync</c>, which resolves a tool only by walking the caller's
    /// <see cref="CapabilityEnvelope.AllowedMcpServers"/> — so the granted envelope built here must name
    /// this same server, or every invocation test would see a governance-refused tool rather than
    /// whatever behaviour it means to exercise.
    /// </summary>
    private const string TestServerName = "test-server";

    /// <summary>
    /// Creates a factory variant that registers a fake <see cref="IMcpToolProvider"/>, a log-capturing
    /// provider, and — #481 — an operator-configured envelope explicitly granting
    /// <paramref name="toolName"/> through <see cref="TestServerName"/>. Exercises the "operator
    /// narrowed this caller's grant" path; <see cref="InvokeTool_NoOperatorConfig_StillWorksByDefault"/>
    /// exercises the unconfigured default instead. <c>DirectToolInvocationConfig.McpEnabled</c> is on
    /// by default so, unlike the keyed-DI surface, no config override is needed to switch this on.
    /// </summary>
    private (HttpClient client, TestLoggerProvider logs) CreateClientWithFakeTool(
        string toolName,
        Func<ValueTask<object?>>? invokeImpl = null,
        bool throwOnInvoke = false)
    {
        var logs = new TestLoggerProvider();
        var fakeProvider = BuildFakeToolProvider(toolName, invokeImpl, throwOnInvoke);

        var factory = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });
                services.AddSingleton<IMcpToolProvider>(fakeProvider);
                services.AddSingleton<ILoggerProvider>(logs);
                services.Replace(ServiceDescriptor.Singleton<ICapabilityEnvelopeResolver>(
                    new FixedEnvelopeResolver(new CapabilityEnvelope
                    {
                        AllowedTools = [toolName],
                        AllowedMcpServers = [TestServerName],
                        // A CapabilityEnvelope grant alone maps to "Ask", not "Autonomous" — see
                        // CapabilityEnvelope.AutonomyCeiling's remarks. This harness wires no approval
                        // routing, so anything short of Autonomous fails closed as Denied rather than
                        // exercising the invocation path these tests mean to cover.
                        AutonomyCeiling = AutonomyLevel.Autonomous
                    })));
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "mcp-test-user");
        return (client, logs);
    }

    /// <summary>
    /// Same as <see cref="CreateClientWithFakeTool"/> but leaves <see cref="ICapabilityEnvelopeResolver"/>
    /// as the host's real, unconfigured default — which resolves to an empty grant — so the request
    /// exercises <c>McpController.ResolveMcpEnvelopeAsync</c>'s own fallback rather than a test double's
    /// grant.
    /// </summary>
    private (HttpClient client, TestLoggerProvider logs) CreateClientWithFakeToolAndNoEnvelopeOverride(
        string toolName, Func<ValueTask<object?>>? invokeImpl = null)
    {
        var logs = new TestLoggerProvider();
        var fakeProvider = BuildFakeToolProvider(toolName, invokeImpl, throwOnInvoke: false);

        var factory = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });
                services.AddSingleton<IMcpToolProvider>(fakeProvider);
                services.AddSingleton<ILoggerProvider>(logs);
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "mcp-test-user-noconfig");
        return (client, logs);
    }

    private static IMcpToolProvider BuildFakeToolProvider(
        string toolName,
        Func<ValueTask<object?>>? invokeImpl,
        bool throwOnInvoke)
    {
        var fn = new FakeAIFunction(toolName, "A test tool for integration tests",
            throwOnInvoke
                ? () => ValueTask.FromException<object?>(new InvalidOperationException("Simulated tool failure"))
                : invokeImpl);

        var mock = new Mock<IMcpToolProvider>();
        mock.Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>>
            {
                [TestServerName] = new List<AITool> { fn },
            });
        mock.Setup(p => p.GetToolByNameAsync(toolName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fn);
        mock.Setup(p => p.GetToolByNameAsync(
                It.Is<string>(n => n != toolName), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AIFunction?)null);

        // #481: IDirectToolInvoker.InvokeMcpToolAsync resolves a tool by walking the envelope's granted
        // servers via GetToolsAsync, never GetToolByNameAsync (which would search every configured
        // server regardless of grant) — so the fake must answer this call too, not just the one the
        // controller's old direct-provider path used.
        mock.Setup(p => p.GetToolsAsync(TestServerName, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<AITool>)new List<AITool> { fn });
        mock.Setup(p => p.GetToolsAsync(
                It.Is<string>(n => n != TestServerName), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<AITool>)new List<AITool>());

        return mock.Object;
    }

    /// <summary>A fixed, always-granting <see cref="ICapabilityEnvelopeResolver"/> test double.</summary>
    private sealed class FixedEnvelopeResolver(CapabilityEnvelope envelope) : ICapabilityEnvelopeResolver
    {
        public CapabilityEnvelope Resolve(ClaimsPrincipal? principal) => envelope;
    }

    // ── Tests: Tool listing ───────────────────────────────────────────────────

    /// <summary>GET /api/mcp/tools returns 200 with a JSON array.</summary>
    [Fact]
    public async Task GetTools_Returns200WithToolList()
    {
        var (client, _) = CreateClientWithFakeTool("list-tool");
        using var response = await client.GetAsync("/api/mcp/tools");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().StartWith("[");
    }

    /// <summary>Each tool in the response carries Name, Description, and Schema fields.</summary>
    [Fact]
    public async Task GetTools_EachToolHasNameDescriptionAndSchema()
    {
        var (client, _) = CreateClientWithFakeTool("schema-tool");
        using var response = await client.GetAsync("/api/mcp/tools");
        var tools = await response.Content.ReadFromJsonAsync<List<McpToolDto>>();

        tools.Should().NotBeNull().And.NotBeEmpty();
        foreach (var tool in tools!)
        {
            tool.Name.Should().NotBeNullOrWhiteSpace();
            tool.Description.Should().NotBeNullOrWhiteSpace();
        }
    }

    // ── Tests: Prompts ────────────────────────────────────────────────────────

    /// <summary>GET /api/mcp/prompts returns 200 with an empty array when no real provider is registered.</summary>
    [Fact]
    public async Task GetPrompts_ReturnsEmptyArrayWhenNoProviderRegistered()
    {
        using var client = _factory.CreateAuthedClient("mcp-test-user");
        using var response = await client.GetAsync("/api/mcp/prompts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Trim().Should().Be("[]");
    }

    // ── Tests: Tool invocation ────────────────────────────────────────────────

    /// <summary>
    /// POST /api/mcp/tools/{name}/invoke works with no operator configuration at all — no
    /// <c>DirectToolInvocationConfig.McpEnabled</c> override, no <see cref="ICapabilityEnvelopeResolver"/>
    /// grant — because both default open for MCP invocation specifically, unlike the keyed-DI direct
    /// invocation surface. Guards against a regression to the keyed-DI surface's deny-by-default posture.
    /// </summary>
    [Fact]
    public async Task InvokeTool_NoOperatorConfig_StillWorksByDefault()
    {
        var (client, _) = CreateClientWithFakeToolAndNoEnvelopeOverride(
            "default-open-tool", invokeImpl: () => ValueTask.FromResult<object?>("tool result"));

        var body = new StringContent(
            JsonSerializer.Serialize(new { Arguments = new { } }),
            Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/mcp/tools/default-open-tool/invoke", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<McpToolInvokeResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
    }

    /// <summary>
    /// Review finding: <c>ResolveMcpEnvelopeAsync</c> used to skip its auto-open fallback whenever
    /// <c>AllowedTools</c> was non-empty, even if <c>AllowedMcpServers</c> was empty — but
    /// <c>AllowedTools</c> is shared with the unrelated keyed-DI direct-invocation surface, so an
    /// operator who narrowed only that grant (naming a tool that has nothing to do with MCP, and
    /// configuring no MCP servers at all) would have silently suppressed the MCP-specific fallback too.
    /// This proves the fallback still opens when only <c>AllowedTools</c> is populated.
    /// </summary>
    [Fact]
    public async Task InvokeTool_EnvelopeGrantsOnlyUnrelatedKeyedDiTool_StillFallsBackToAutoOpenForMcp()
    {
        var logs = new TestLoggerProvider();
        var fakeProvider = BuildFakeToolProvider("mcp-only-tool", () => ValueTask.FromResult<object?>("tool result"), throwOnInvoke: false);

        var factory = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });
                services.AddSingleton<IMcpToolProvider>(fakeProvider);
                services.AddSingleton<ILoggerProvider>(logs);
                services.Replace(ServiceDescriptor.Singleton<ICapabilityEnvelopeResolver>(
                    new FixedEnvelopeResolver(new CapabilityEnvelope
                    {
                        // Names a keyed-DI tool that has nothing to do with MCP; AllowedMcpServers is
                        // deliberately left empty so this exercises the auto-open fallback condition.
                        AllowedTools = ["some-unrelated-keyed-di-tool"],
                        AllowedMcpServers = [],
                        AutonomyCeiling = AutonomyLevel.Autonomous
                    })));
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "mcp-narrowed-keyeddi-user");

        var body = new StringContent(
            JsonSerializer.Serialize(new { Arguments = new { } }),
            Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/mcp/tools/mcp-only-tool/invoke", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<McpToolInvokeResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
    }

    /// <summary>POST /api/mcp/tools/{name}/invoke returns 200 with Success=true for a working tool.</summary>
    [Fact]
    public async Task InvokeTool_ValidArgs_Returns200WithOutput()
    {
        var (client, _) = CreateClientWithFakeTool(
            "working-tool",
            invokeImpl: () => ValueTask.FromResult<object?>("tool result"));

        var body = new StringContent(
            JsonSerializer.Serialize(new { Arguments = new { } }),
            Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/mcp/tools/working-tool/invoke", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<McpToolInvokeResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
    }

    /// <summary>
    /// The dominant MCP tool-success shape (see <c>ToolResultTextTests</c>'s header remarks, confirmed
    /// by decompiling <c>ModelContextProtocol.Core</c>'s <c>McpClientTool.InvokeCoreAsync</c>): a
    /// single-content-block success reaches <see cref="Application.AI.Common.Services.Tools.DirectToolInvoker"/>
    /// as a bare <see cref="TextContent"/>, not a plain string. Before <c>ToolResultText.ExtractText</c>
    /// existed, the invoker's own reduction fell through to serializing the whole <see cref="TextContent"/>
    /// object as JSON instead of extracting its <c>Text</c> — this proves the real end-to-end HTTP path
    /// returns the tool's actual text for the shape a real MCP tool call is most likely to produce.
    /// </summary>
    [Fact]
    public async Task InvokeTool_ToolReturnsTextContent_ExtractsItsTextRatherThanSerializingTheObject()
    {
        var (client, _) = CreateClientWithFakeTool(
            "text-content-tool",
            invokeImpl: () => ValueTask.FromResult<object?>(new TextContent("the tool's actual answer")));

        var body = new StringContent(
            JsonSerializer.Serialize(new { Arguments = new { } }),
            Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/mcp/tools/text-content-tool/invoke", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<McpToolInvokeResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Output.Should().Be("the tool's actual answer");
    }

    /// <summary>POST /api/mcp/tools/{name}/invoke returns 404 for an unknown tool name.</summary>
    [Fact]
    public async Task InvokeTool_UnknownTool_Returns404()
    {
        var (client, _) = CreateClientWithFakeTool("known-tool");
        var body = new StringContent(
            JsonSerializer.Serialize(new { Arguments = new { } }),
            Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/mcp/tools/nonexistent-tool/invoke", body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// POST /api/mcp/tools/{name}/invoke returns 200 with Success=false when the MCP tool reports a
    /// protocol-level failure (<c>isError: true</c>) without throwing — the shape
    /// <see cref="Application.AI.Common.Services.Tools.McpFailureNormalizingAIFunction"/> recognizes
    /// and DirectToolInvoker's response shaping treats as a completed, "the tool said no" invocation.
    /// </summary>
    [Fact]
    public async Task InvokeTool_ToolReportsProtocolFailure_Returns200WithSuccessFalse()
    {
        var mcpFailure = JsonSerializer.SerializeToElement(new
        {
            isError = true,
            content = new[] { new { type = "text", text = "Simulated tool failure" } }
        });
        var (client, _) = CreateClientWithFakeTool(
            "failing-tool", invokeImpl: () => ValueTask.FromResult<object?>(mcpFailure));
        var body = new StringContent(
            JsonSerializer.Serialize(new { Arguments = new { } }),
            Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/mcp/tools/failing-tool/invoke", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<McpToolInvokeResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.Output.Should().BeNull("error responses must not populate Output");
        result.Error.Should().Contain("Simulated tool failure");
    }

    /// <summary>
    /// POST /api/mcp/tools/{name}/invoke returns 500 when the tool call itself throws — distinct from a
    /// protocol-level failure, and deliberately does not echo the exception message across the trust
    /// boundary (matches <c>ToolsController.Invoke</c>'s keyed-DI convention).
    /// </summary>
    [Fact]
    public async Task InvokeTool_ToolThrows_Returns500Faulted()
    {
        var (client, _) = CreateClientWithFakeTool("throwing-tool", throwOnInvoke: true);
        var body = new StringContent(
            JsonSerializer.Serialize(new { Arguments = new { } }),
            Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/mcp/tools/throwing-tool/invoke", body);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    /// <summary>POST /api/mcp/tools/{name}/invoke emits a structured audit log entry with UserId, ToolName, InputHash.</summary>
    [Fact]
    public async Task InvokeTool_EmitsStructuredAuditLog()
    {
        var (client, logs) = CreateClientWithFakeTool("audit-tool");
        var body = new StringContent(
            JsonSerializer.Serialize(new { Arguments = new { key = "value" } }),
            Encoding.UTF8, "application/json");

        using var _ = await client.PostAsync("/api/mcp/tools/audit-tool/invoke", body);

        logs.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("audit-tool") &&
            e.Message.Contains("InputHash"));
    }

    /// <summary>
    /// An Entra caller whose token carries only <c>oid</c> is attributed by that oid, not logged as
    /// "anonymous".
    /// </summary>
    /// <remarks>
    /// This is the defect the audit trail actually shipped with: the controller hand-rolled a
    /// NameIdentifier lookup, so the most common real Entra token shape — oid and nothing else —
    /// produced an audit line claiming the tool was invoked anonymously. A trail that misattributes a
    /// known caller is worse than one that admits it does not know, because it reads as an answer.
    /// </remarks>
    [Fact]
    public async Task InvokeTool_OidOnlyToken_IsAttributedToTheOid_NotAnonymous()
    {
        var (client, logs) = CreateClientWithFakeTool("attribution-tool");
        client.DefaultRequestHeaders.Remove(TestAuthHandler.UserIdHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "entra-oid-caller");
        client.DefaultRequestHeaders.Add(TestAuthHandler.ClaimShapeHeader, "oid-only");

        var body = new StringContent(
            JsonSerializer.Serialize(new { Arguments = new { key = "value" } }),
            Encoding.UTF8, "application/json");

        using var _ = await client.PostAsync("/api/mcp/tools/attribution-tool/invoke", body);

        var audit = logs.Entries.Where(e =>
            e.Level == LogLevel.Information && e.Message.Contains("MCP tool invoked")).ToList();

        audit.Should().ContainSingle();
        audit[0].Message.Should().Contain("entra-oid-caller");
        audit[0].Message.Should().NotContain("anonymous");
    }

    /// <summary>POST body exceeding 32 KB returns 413 Request Entity Too Large.</summary>
    [Fact]
    public async Task InvokeTool_OversizedBody_Returns413()
    {
        using var client = _factory.CreateAuthedClient("mcp-test-user");

        // 33 KB of JSON — safely over the 32 KB limit.
        var oversized = new string('x', 33 * 1024);
        var body = new StringContent($"{{\"Arguments\":{{\"data\":\"{oversized}\"}}}}", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/mcp/tools/any-tool/invoke", body);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    /// <summary>Audit log entries at Information level do not contain raw argument values.</summary>
    [Fact]
    public async Task InvokeTool_AuditLog_DoesNotContainRawArgumentsAtInfoLevel()
    {
        var (client, logs) = CreateClientWithFakeTool("no-leak-tool");
        const string sensitiveValue = "super-secret-argument-value";
        var body = new StringContent(
            JsonSerializer.Serialize(new { Arguments = new { secret = sensitiveValue } }),
            Encoding.UTF8, "application/json");

        using var _ = await client.PostAsync("/api/mcp/tools/no-leak-tool/invoke", body);

        var infoEntries = logs.Entries
            .Where(e => e.Level == LogLevel.Information)
            .ToList();

        infoEntries.Should().NotBeEmpty("audit log entry must exist at Information level");
        infoEntries.Should().NotContain(e => e.Message.Contains(sensitiveValue),
            "raw argument values must not appear in Information-level log entries");
    }

    // ── Nested helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Concrete <see cref="AIFunction"/> subclass used as a test double.
    /// Implements only the members required by <see cref="Presentation.AgentHub.Controllers.McpController"/>.
    /// </summary>
    private sealed class FakeAIFunction : AIFunction
    {
        private readonly Func<ValueTask<object?>>? _impl;

        public override string Name { get; }
        public override string Description { get; }
        public override JsonElement JsonSchema { get; }

        public FakeAIFunction(string name, string description, Func<ValueTask<object?>>? impl = null)
        {
            Name = name;
            Description = description;
            JsonSchema = JsonSerializer.SerializeToElement(new { type = "object" });
            _impl = impl;
        }

        // InvokeCoreAsync is called by AIFunction.InvokeAsync after argument marshaling.
        // Returning a faulted ValueTask here is properly propagated by the base class.
        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            if (_impl is null)
                return ValueTask.FromResult<object?>("ok");
            return _impl();
        }
    }

    /// <summary>
    /// Captures log entries written during a request for assertion in audit log tests.
    /// Register as a singleton <see cref="ILoggerProvider"/> in <c>ConfigureTestServices</c>.
    /// </summary>
    internal sealed class TestLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(TestLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => provider.Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

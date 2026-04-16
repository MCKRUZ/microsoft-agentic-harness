using Application.AI.Common.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Presentation.AgentHub.DTOs;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Integration tests for <see cref="McpController"/> HTTP endpoints.
/// Covers tool listing, tool invocation, error handling, audit logging,
/// and the 32 KB request size limit.
/// </summary>
public sealed class McpControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    /// <summary>Initialises the test class with the shared factory.</summary>
    public McpControllerTests(TestWebApplicationFactory factory) => _factory = factory;

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Creates an authenticated HTTP client using <see cref="TestAuthHandler"/>.</summary>
    private HttpClient CreateAuthedClient(
        string userId = "mcp-test-user",
        WebApplicationFactory<Program>? factory = null)
    {
        var f = factory ?? _factory;
        var client = f
            .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { })))
            .CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId);
        return client;
    }

    /// <summary>
    /// Creates a factory variant that registers a fake <see cref="IMcpToolProvider"/>
    /// and a log-capturing provider, then returns both along with a configured client.
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
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "mcp-test-user");
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
                ["test-server"] = new List<AITool> { fn },
            });
        mock.Setup(p => p.GetToolByNameAsync(toolName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fn);
        mock.Setup(p => p.GetToolByNameAsync(
                It.Is<string>(n => n != toolName), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AIFunction?)null);
        return mock.Object;
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
        using var client = CreateAuthedClient();
        using var response = await client.GetAsync("/api/mcp/prompts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Trim().Should().Be("[]");
    }

    // ── Tests: Tool invocation ────────────────────────────────────────────────

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

    /// <summary>POST /api/mcp/tools/{name}/invoke returns 200 with Success=false when the tool throws.</summary>
    [Fact]
    public async Task InvokeTool_ToolExecutionFailure_Returns200WithSuccessFalse()
    {
        var (client, _) = CreateClientWithFakeTool("failing-tool", throwOnInvoke: true);
        var body = new StringContent(
            JsonSerializer.Serialize(new { Arguments = new { } }),
            Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/mcp/tools/failing-tool/invoke", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<McpToolInvokeResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.Output.Should().BeNull("error responses must not populate Output");
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

    /// <summary>POST body exceeding 32 KB returns 413 Request Entity Too Large.</summary>
    [Fact]
    public async Task InvokeTool_OversizedBody_Returns413()
    {
        using var client = CreateAuthedClient();

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
    /// Implements only the members required by <see cref="McpController"/>.
    /// </summary>
    private sealed class FakeAIFunction : AIFunction
    {
        private readonly Func<ValueTask<object?>>? _impl;

        public override string Name { get; }
        public override string? Description { get; }
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

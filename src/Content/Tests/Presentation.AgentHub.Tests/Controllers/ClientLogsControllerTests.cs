using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Presentation.AgentHub.DTOs;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

public sealed class ClientLogsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ClientLogsControllerTests(TestWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateAuthedClient(string userId = "client-log-user")
    {
        var client = _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { })))
            .CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId);
        return client;
    }

    [Fact]
    public async Task Post_NullBody_Returns400()
    {
        using var client = CreateAuthedClient();

        var response = await client.PostAsJsonAsync<IReadOnlyList<ClientLogEntry>?>(
            "/api/client-logs", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_EmptyArray_Returns400()
    {
        using var client = CreateAuthedClient();

        var response = await client.PostAsJsonAsync("/api/client-logs",
            Array.Empty<ClientLogEntry>());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_ValidEntries_Returns202()
    {
        using var client = CreateAuthedClient();
        var entries = new[]
        {
            new ClientLogEntry("sess-1", "info", "Test message",
                DateTimeOffset.UtcNow, "http://localhost", null)
        };

        var response = await client.PostAsJsonAsync("/api/client-logs", entries);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Post_OverMaxBatchSize_Returns413()
    {
        using var client = CreateAuthedClient();

        // MaxBatchSize is 100; send 101 entries
        var entries = Enumerable.Range(0, 101)
            .Select(i => new ClientLogEntry("sess-1", "info", $"msg-{i}",
                DateTimeOffset.UtcNow, null, null))
            .ToArray();

        var response = await client.PostAsJsonAsync("/api/client-logs", entries);

        // HttpStatusCode.PayloadTooLarge is not available in all TFMs; use the numeric value
        response.StatusCode.Should().Be((HttpStatusCode)413);
    }

    [Fact]
    public async Task Post_MultipleValidEntries_Returns202()
    {
        using var client = CreateAuthedClient();
        var entries = new[]
        {
            new ClientLogEntry("sess-1", "debug", "Debug msg",
                DateTimeOffset.UtcNow, null, null),
            new ClientLogEntry("sess-1", "error", "Error msg",
                DateTimeOffset.UtcNow, "http://localhost/page", "stack trace here"),
            new ClientLogEntry("sess-1", "warn", "Warning msg",
                DateTimeOffset.UtcNow, null, null),
        };

        var response = await client.PostAsJsonAsync("/api/client-logs", entries);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Post_ExactlyMaxBatchSize_Returns202()
    {
        using var client = CreateAuthedClient();

        var entries = Enumerable.Range(0, 100)
            .Select(i => new ClientLogEntry("sess-1", "info", $"msg-{i}",
                DateTimeOffset.UtcNow, null, null))
            .ToArray();

        var response = await client.PostAsJsonAsync("/api/client-logs", entries);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Post_UnknownLogLevel_TreatedAsInformation()
    {
        using var client = CreateAuthedClient();
        var entries = new[]
        {
            new ClientLogEntry("sess-1", "unknown-level", "Unknown level msg",
                DateTimeOffset.UtcNow, null, null)
        };

        // Should not fail — unknown levels map to Information
        var response = await client.PostAsJsonAsync("/api/client-logs", entries);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }
}

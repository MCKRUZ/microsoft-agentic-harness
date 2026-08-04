using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Moq;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.Controllers;
using Presentation.AgentHub.Interfaces;
using System.Net;
using System.Net.Http.Json;
using Xunit;
using Application.AI.Common.Interfaces.AI;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Integration tests for <c>POST /api/conversations</c> — the production conversation-create endpoint
/// the dashboard agent panel calls before opening an AG-UI run. Verifies the new record is owned by the
/// caller and bound to the requested agent, with the configured default used as the fallback.
/// </summary>
public sealed class AgentsControllerCreateConversationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly IConversationStore _store;

    public AgentsControllerCreateConversationTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _store = factory.Services.GetRequiredService<IConversationStore>();
    }

    /// <summary>
    /// Builds an authenticated client whose <see cref="AgentHubConfig.DefaultAgentName"/> is pinned to
    /// <paramref name="defaultAgentName"/>, so the fallback behavior is deterministic regardless of the
    /// host's ambient configuration.
    /// </summary>
    private HttpClient CreateClientAs(string userId, string defaultAgentName)
    {
        var options = new Mock<IOptionsMonitor<AgentHubConfig>>();
        options.Setup(m => m.CurrentValue).Returns(new AgentHubConfig { DefaultAgentName = defaultAgentName });

        var client = _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
                services.RemoveAll<IOptionsMonitor<AgentHubConfig>>();
                services.AddSingleton(options.Object);
            }))
            .CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId);
        return client;
    }

    [Fact]
    public async Task CreateConversation_WithAgentName_CreatesRecordOwnedByCaller_AndReturnsThreadId()
    {
        var userId = $"create-user-{Guid.NewGuid():N}";
        using var client = CreateClientAs(userId, defaultAgentName: "");

        var response = await client.PostAsJsonAsync("/api/conversations", new { agentName = "dashboard-agent" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<CreateConversationResponse>();
        body.Should().NotBeNull();
        body!.AgentName.Should().Be("dashboard-agent");
        body.ThreadId.Should().NotBeNullOrWhiteSpace();

        var stored = await _store.GetAsync(body.ThreadId, userId);
        stored.Should().NotBeNull();
        stored!.UserId.Should().Be(userId, "the conversation must be owned by the caller");
        stored.AgentName.Should().Be("dashboard-agent");
    }

    [Fact]
    public async Task CreateConversation_NoAgentName_FallsBackToConfiguredDefault()
    {
        var userId = $"create-default-{Guid.NewGuid():N}";
        using var client = CreateClientAs(userId, defaultAgentName: "fallback-agent");

        var response = await client.PostAsJsonAsync("/api/conversations", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<CreateConversationResponse>();
        body!.AgentName.Should().Be("fallback-agent");
    }

    [Fact]
    public async Task CreateConversation_NoAgentNameAndNoDefault_Returns400()
    {
        var userId = $"create-noagent-{Guid.NewGuid():N}";
        using var client = CreateClientAs(userId, defaultAgentName: "");

        var response = await client.PostAsJsonAsync("/api/conversations", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

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
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Agents;
using Domain.AI.Governance;
using Domain.AI.Orchestration;

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

    /// <summary>
    /// Builds an authenticated client with <see cref="AgentHubConfig.DefaultAgentName"/> pinned and
    /// <see cref="IAgentRouter"/> replaced by <paramref name="router"/>, so the router-dispatch branch
    /// in <c>AgentsController.ResolveAgentNameAsync</c> can be tested in isolation — the router's
    /// own classification/matching logic has its own dedicated unit tests elsewhere.
    /// </summary>
    private HttpClient CreateClientAs(string userId, string defaultAgentName, IAgentRouter router)
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
                services.RemoveAll<IAgentRouter>();
                services.AddSingleton(router);
            }))
            .CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId);
        return client;
    }

    private static AgentSelection Selection(string agentId) => new()
    {
        SelectedAgent = new AgentCandidate
        {
            AgentId = agentId,
            AgentType = SubagentType.NamedAgent,
            AutonomyLevel = AutonomyLevel.Restricted,
            AvailableTools = []
        },
        ConfidenceScore = 0.9,
        Reasoning = "test selection"
    };

    [Fact]
    public async Task CreateConversation_NoAgentNameWithFirstMessage_UsesRouterSelection()
    {
        var userId = $"create-routed-{Guid.NewGuid():N}";
        var mockRouter = new Mock<IAgentRouter>();
        mockRouter
            .Setup(r => r.RouteAsync("find prior art for this approach", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Selection("research-agent"));
        using var client = CreateClientAs(userId, defaultAgentName: "fallback-agent", mockRouter.Object);

        var response = await client.PostAsJsonAsync(
            "/api/conversations", new { firstMessage = "find prior art for this approach" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<CreateConversationResponse>();
        body!.AgentName.Should().Be("research-agent");
    }

    [Fact]
    public async Task CreateConversation_RouterDeclines_FallsBackToConfiguredDefault()
    {
        var userId = $"create-declined-{Guid.NewGuid():N}";
        var mockRouter = new Mock<IAgentRouter>();
        mockRouter
            .Setup(r => r.RouteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentSelection?)null);
        using var client = CreateClientAs(userId, defaultAgentName: "fallback-agent", mockRouter.Object);

        var response = await client.PostAsJsonAsync("/api/conversations", new { firstMessage = "hmm" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<CreateConversationResponse>();
        body!.AgentName.Should().Be("fallback-agent");
    }

    [Fact]
    public async Task CreateConversation_ExplicitAgentNameWithFirstMessage_AgentNameWinsWithoutCallingRouter()
    {
        var userId = $"create-explicit-{Guid.NewGuid():N}";
        var mockRouter = new Mock<IAgentRouter>();
        using var client = CreateClientAs(userId, defaultAgentName: "fallback-agent", mockRouter.Object);

        var response = await client.PostAsJsonAsync(
            "/api/conversations", new { agentName = "dashboard-agent", firstMessage = "irrelevant" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<CreateConversationResponse>();
        body!.AgentName.Should().Be("dashboard-agent");
        mockRouter.Verify(r => r.RouteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

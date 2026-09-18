using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Presentation.AgentHub.Controllers;
using System.Net;
using System.Net.Http.Json;
using Xunit;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Models.Conversations;
using Domain.AI.Agents;
using Domain.AI.Governance;
using Domain.AI.Orchestration;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Integration tests for <c>PATCH /api/conversations/{id}/agent</c> — the explicit re-route endpoint
/// on an existing conversation.
/// </summary>
public sealed class AgentsControllerReassignAgentTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly IConversationStore _store;

    public AgentsControllerReassignAgentTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _store = factory.Services.GetRequiredService<IConversationStore>();
    }

    private HttpClient CreateClientAs(string userId, IAgentRouter? router = null)
    {
        var client = _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
                if (router is not null)
                {
                    services.RemoveAll<IAgentRouter>();
                    services.AddSingleton(router);
                }
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
    public async Task ReassignAgent_ExplicitAgentName_RebindsWithoutCallingRouter()
    {
        var userId = $"reassign-explicit-{Guid.NewGuid():N}";
        var record = await _store.CreateAsync("dashboard-agent", userId);
        var mockRouter = new Mock<IAgentRouter>();
        using var client = CreateClientAs(userId, mockRouter.Object);

        var response = await client.PatchAsJsonAsync(
            $"/api/conversations/{record.Id}/agent", new { agentName = "research-agent" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CreateConversationResponse>();
        body!.AgentName.Should().Be("research-agent");
        (await _store.GetAsync(record.Id, userId))!.AgentName.Should().Be("research-agent");
        mockRouter.Verify(r => r.RouteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReassignAgent_NoAgentName_ReRoutesFromLatestUserMessage()
    {
        var userId = $"reassign-routed-{Guid.NewGuid():N}";
        var record = await _store.CreateAsync("dashboard-agent", userId);
        await _store.AppendMessagesAsync(record.Id, userId, [
            new ConversationMessage(Guid.NewGuid(), MessageRole.User, "find prior art", DateTimeOffset.UtcNow),
            new ConversationMessage(Guid.NewGuid(), MessageRole.Assistant, "sure, one moment", DateTimeOffset.UtcNow),
        ]);
        var mockRouter = new Mock<IAgentRouter>();
        mockRouter
            .Setup(r => r.RouteAsync("find prior art", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Selection("research-agent"));
        using var client = CreateClientAs(userId, mockRouter.Object);

        var response = await client.PatchAsJsonAsync($"/api/conversations/{record.Id}/agent", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CreateConversationResponse>();
        body!.AgentName.Should().Be("research-agent");
    }

    [Fact]
    public async Task ReassignAgent_NoAgentNameAndRouterDeclines_Returns400()
    {
        var userId = $"reassign-declined-{Guid.NewGuid():N}";
        var record = await _store.CreateAsync("dashboard-agent", userId);
        await _store.AppendMessageAsync(record.Id, userId,
            new ConversationMessage(Guid.NewGuid(), MessageRole.User, "hmm", DateTimeOffset.UtcNow));
        var mockRouter = new Mock<IAgentRouter>();
        mockRouter
            .Setup(r => r.RouteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentSelection?)null);
        using var client = CreateClientAs(userId, mockRouter.Object);

        var response = await client.PatchAsJsonAsync($"/api/conversations/{record.Id}/agent", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _store.GetAsync(record.Id, userId))!.AgentName.Should().Be("dashboard-agent");
    }

    [Fact]
    public async Task ReassignAgent_NoAgentNameAndNoUserMessage_Returns400WithoutCallingRouter()
    {
        var userId = $"reassign-empty-{Guid.NewGuid():N}";
        var record = await _store.CreateAsync("dashboard-agent", userId);
        var mockRouter = new Mock<IAgentRouter>();
        using var client = CreateClientAs(userId, mockRouter.Object);

        var response = await client.PatchAsJsonAsync($"/api/conversations/{record.Id}/agent", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        mockRouter.Verify(r => r.RouteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReassignAgent_NonexistentConversation_Returns404()
    {
        var userId = $"reassign-missing-{Guid.NewGuid():N}";
        using var client = CreateClientAs(userId);

        var response = await client.PatchAsJsonAsync(
            $"/api/conversations/{Guid.NewGuid():N}/agent", new { agentName = "research-agent" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReassignAgent_OwnedByAnotherUser_Returns403()
    {
        var owner = $"reassign-owner-{Guid.NewGuid():N}";
        var stranger = $"reassign-stranger-{Guid.NewGuid():N}";
        var record = await _store.CreateAsync("dashboard-agent", owner);
        using var client = CreateClientAs(stranger);

        var response = await client.PatchAsJsonAsync(
            $"/api/conversations/{record.Id}/agent", new { agentName = "research-agent" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _store.GetAsync(record.Id, owner))!.AgentName.Should().Be("dashboard-agent");
    }
}

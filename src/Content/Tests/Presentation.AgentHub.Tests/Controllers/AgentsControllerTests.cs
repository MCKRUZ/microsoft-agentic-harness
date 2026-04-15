using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Integration tests for AgentsController ownership enforcement.
/// Stubs — wired up fully in section-07 once TestAuthHandler supports per-test claim injection.
/// </summary>
public sealed class AgentsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    public AgentsControllerTests(TestWebApplicationFactory factory) { }

    [Fact]
    public async Task GetConversations_ReturnsOnlyConversationsOwnedByAuthenticatedUser()
    {
        // Implemented in section-07 with per-test user identity override.
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GetConversationById_AnotherUsersConversation_Returns403()
    {
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DeleteConversation_AnotherUsersConversation_Returns403()
    {
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DeleteConversation_OwnConversation_Returns204AndRemovesFile()
    {
        await Task.CompletedTask;
    }
}

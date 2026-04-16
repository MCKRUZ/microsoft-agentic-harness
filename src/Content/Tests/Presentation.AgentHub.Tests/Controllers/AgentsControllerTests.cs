using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Models;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Integration tests for <see cref="AgentsController"/> ownership enforcement.
/// Verifies that IDOR vulnerabilities are prevented at the HTTP layer: users may only
/// read or delete conversations where <see cref="ConversationRecord.UserId"/> matches
/// their own identity.
/// </summary>
public sealed class AgentsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly IConversationStore _store;

    /// <summary>Initialises the test class with the shared factory and resolves the conversation store.</summary>
    public AgentsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _store = factory.Services.GetRequiredService<IConversationStore>();
    }

    /// <summary>Creates an HTTP client authenticated as <paramref name="userId"/> via the test auth handler.</summary>
    private HttpClient CreateClientAs(string userId)
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

    /// <summary>
    /// GET /api/conversations returns only the conversations owned by the authenticated user,
    /// not those belonging to other users.
    /// </summary>
    [Fact]
    public async Task GetConversations_ReturnsOnlyConversationsOwnedByAuthenticatedUser()
    {
        // Use unique IDs per test run so shared factory state doesn't pollute counts.
        var testId = Guid.NewGuid().ToString("N")[..8];
        var userA = $"conversations-user-a-{testId}";
        var userB = $"conversations-user-b-{testId}";

        await _store.CreateAsync("test-agent", userA);
        await _store.CreateAsync("test-agent", userA);
        await _store.CreateAsync("test-agent", userB);

        using var clientA = CreateClientAs(userA);
        using var clientB = CreateClientAs(userB);

        var respA = await clientA.GetAsync("/api/conversations");
        var respB = await clientB.GetAsync("/api/conversations");

        respA.StatusCode.Should().Be(HttpStatusCode.OK);
        respB.StatusCode.Should().Be(HttpStatusCode.OK);

        var convA = await respA.Content.ReadFromJsonAsync<List<ConversationRecord>>();
        var convB = await respB.Content.ReadFromJsonAsync<List<ConversationRecord>>();

        convA.Should().NotBeNull().And.HaveCount(2);
        convB.Should().NotBeNull().And.HaveCount(1);
        convA!.Should().OnlyContain(c => c.UserId == userA);
        convB!.Should().OnlyContain(c => c.UserId == userB);
    }

    /// <summary>
    /// GET /api/conversations/{id} returns 403 when the conversation belongs to a different user.
    /// </summary>
    [Fact]
    public async Task GetConversationById_AnotherUsersConversation_Returns403()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        var owner = $"get-owner-{testId}";
        var attacker = $"get-attacker-{testId}";

        var ownerConv = await _store.CreateAsync("test-agent", owner);

        using var client = CreateClientAs(attacker);
        var response = await client.GetAsync($"/api/conversations/{ownerConv.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// DELETE /api/conversations/{id} returns 403 when the conversation belongs to a different user.
    /// </summary>
    [Fact]
    public async Task DeleteConversation_AnotherUsersConversation_Returns403()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        var owner = $"del-owner-{testId}";
        var attacker = $"del-attacker-{testId}";

        var ownerConv = await _store.CreateAsync("test-agent", owner);

        using var client = CreateClientAs(attacker);
        var response = await client.DeleteAsync($"/api/conversations/{ownerConv.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// DELETE /api/conversations/{id} returns 204 and removes the conversation record for the owning user.
    /// </summary>
    [Fact]
    public async Task DeleteConversation_OwnConversation_Returns204AndRemovesConversation()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        var owner = $"del-self-{testId}";

        var conv = await _store.CreateAsync("test-agent", owner);

        using var client = CreateClientAs(owner);
        var response = await client.DeleteAsync($"/api/conversations/{conv.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var deleted = await _store.GetAsync(conv.Id);
        deleted.Should().BeNull("the conversation must be removed from the store after deletion");
    }
}

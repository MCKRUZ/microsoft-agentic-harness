using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Presentation.AgentHub.Controllers;
using System.Net;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Regression tests for the IDOR fix on <see cref="SessionsController"/>.
///
/// The session observability endpoints return <b>global, cross-user</b> data —
/// the underlying <c>IObservabilityStore</c> queries carry no caller identity, so
/// any response can surface every user's conversation content, tool args/stdout,
/// and composed prompt bodies. Before the fix the controller was gated only by
/// <c>[Authorize]</c>, so any authenticated chat user could enumerate and read
/// other users' conversations (horizontal-privilege IDOR). The fix role-gates the
/// controller with <see cref="SessionsController.ObserverRole"/>, mirroring the
/// SignalR equivalent (<c>AgentTelemetryHub.JoinGlobalTraces</c>).
///
/// These tests assert the authorization boundary directly: an authenticated caller
/// <em>without</em> the role is forbidden, and one <em>with</em> the role is allowed.
/// The first test fails on the old <c>[Authorize]</c>-only behavior (it returned 200).
/// </summary>
public sealed class SessionsControllerSolutionReviewFixTests
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    /// <summary>Initialises the test class with the shared integration factory.</summary>
    public SessionsControllerSolutionReviewFixTests(TestWebApplicationFactory factory) =>
        _factory = factory;

    /// <summary>Creates an HTTP client authenticated as <paramref name="userId"/> with the supplied roles.</summary>
    private HttpClient CreateClientAs(string userId, params string[] roles)
    {
        var client = _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });
            }))
            .CreateClient();

        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId);
        if (roles.Length > 0)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
        return client;
    }

    /// <summary>
    /// GET /api/sessions returns 403 for an authenticated caller lacking the observer
    /// role — the IDOR fix. This is the exact horizontal-privilege gap: an ordinary
    /// chat user must not be able to enumerate every user's sessions over HTTP.
    /// </summary>
    [Fact]
    public async Task GetSessions_AuthenticatedWithoutObserverRole_Returns403()
    {
        using var client = CreateClientAs("chat-user-no-role");

        var response = await client.GetAsync("/api/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// GET /api/sessions/{id} returns 403 for an authenticated caller lacking the
    /// observer role — full conversation content (messages, tool stdout, prompt
    /// snapshots) must not be reachable by a non-observer.
    /// </summary>
    [Fact]
    public async Task GetSessionDetail_AuthenticatedWithoutObserverRole_Returns403()
    {
        using var client = CreateClientAs("chat-user-no-role");

        var response = await client.GetAsync($"/api/sessions/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// GET /api/sessions succeeds for a caller holding the observer role — the gate
    /// admits authorized observability consumers (the Foresight dashboard audience),
    /// matching the role-gated SignalR global-traces path.
    /// </summary>
    [Fact]
    public async Task GetSessions_AuthenticatedWithObserverRole_Returns200()
    {
        using var client = CreateClientAs("observer-user", SessionsController.ObserverRole);

        var response = await client.GetAsync("/api/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

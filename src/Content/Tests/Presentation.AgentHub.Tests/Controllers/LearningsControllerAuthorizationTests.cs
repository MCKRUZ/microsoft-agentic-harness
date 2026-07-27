using Application.Core.CQRS.Learnings;
using Domain.AI.Learnings;
using Domain.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Presentation.AgentHub.Controllers;
using System.Net;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Wire-level authorization tests for <see cref="LearningsController"/>.
///
/// The learnings recall endpoint returns <b>cross-user</b> data by construction — learnings are
/// scoped to agent/team/global, never to users, so a single response can surface knowledge
/// captured from any user's corrections, escalations, or drift events. The controller is
/// therefore role-gated with <see cref="LearningsController.OperatorRole"/>, following the
/// <see cref="SessionsController.ObserverRole"/> precedent for cross-user surfaces.
///
/// These tests assert the boundary directly: an unauthenticated caller is challenged, an
/// authenticated caller <em>without</em> the role is forbidden, and one <em>with</em> the role
/// is admitted.
/// </summary>
public sealed class LearningsControllerAuthorizationTests
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    /// <summary>Initialises the test class with the shared integration factory.</summary>
    public LearningsControllerAuthorizationTests(TestWebApplicationFactory factory) =>
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
    /// GET /api/learnings challenges an unauthenticated caller with 401 — the endpoint must not
    /// be reachable anonymously at all.
    /// </summary>
    [Fact]
    public async Task Recall_Unauthenticated_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/learnings?context=anything");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// GET /api/learnings returns 403 for an authenticated caller lacking the operator role —
    /// an ordinary chat user must not be able to read cross-user learnings over HTTP.
    /// </summary>
    [Fact]
    public async Task Recall_AuthenticatedWithoutOperatorRole_Returns403()
    {
        using var client = CreateClientAs("chat-user-no-role");

        var response = await client.GetAsync("/api/learnings?context=anything");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// GET /api/learnings succeeds for a caller holding the operator role — the gate admits
    /// authorized operators inspecting what the system has learned.
    /// </summary>
    [Fact]
    public async Task Recall_AuthenticatedWithOperatorRole_Returns200()
    {
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<RecallLearningsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<WeightedLearning>>.Success(Array.Empty<WeightedLearning>()));

        using var client = CreateClientAs("operator-user", LearningsController.OperatorRole);

        var response = await client.GetAsync("/api/learnings?context=anything");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

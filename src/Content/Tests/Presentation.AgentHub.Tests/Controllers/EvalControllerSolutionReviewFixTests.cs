using Application.AI.Common.CQRS.Evaluation.GetEvalRunHistory;
using Application.AI.Common.Evaluation.Models;
using Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Presentation.AgentHub.Controllers;
using System.Net;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Regression tests for the IDOR fix on <see cref="EvalController"/>.
///
/// The eval endpoints expose <b>global, cross-user</b> evaluation data — run
/// reports, prompt-version comparisons, regressed cases, and trace replays carry
/// no caller identity, so any response can surface every team's eval results and
/// the prompt/trace bodies behind them. Before the fix the controller was gated
/// only by <c>[Authorize]</c>, so any authenticated chat user could enumerate and
/// read other teams' eval data (horizontal-privilege IDOR). The fix role-gates the
/// controller with <see cref="SessionsController.ObserverRole"/>, the same role
/// that gates the equivalent privileged session-observability surface.
///
/// These tests assert the authorization boundary directly: an authenticated caller
/// <em>without</em> the role is forbidden, and one <em>with</em> the role is allowed.
/// The first test fails on the old <c>[Authorize]</c>-only behavior (it returned 200).
/// </summary>
public sealed class EvalControllerSolutionReviewFixTests
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    /// <summary>Initialises the test class with the shared integration factory.</summary>
    public EvalControllerSolutionReviewFixTests(TestWebApplicationFactory factory) =>
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
    /// GET /api/evals/runs returns 403 for an authenticated caller lacking the observer
    /// role — the IDOR fix. This is the exact horizontal-privilege gap: an ordinary chat
    /// user must not be able to enumerate every team's eval runs over HTTP.
    /// </summary>
    [Fact]
    public async Task GetHistory_AuthenticatedWithoutObserverRole_Returns403()
    {
        using var client = CreateClientAs("chat-user-no-role");

        var response = await client.GetAsync("/api/evals/runs");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// GET /api/evals/runs/{runId} returns 403 for an authenticated caller lacking the
    /// observer role — full eval run reports (prompt bodies, scored cases) must not be
    /// reachable by a non-observer.
    /// </summary>
    [Fact]
    public async Task GetDetail_AuthenticatedWithoutObserverRole_Returns403()
    {
        using var client = CreateClientAs("chat-user-no-role");

        var response = await client.GetAsync("/api/evals/runs/some-run-id");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// GET /api/evals/runs succeeds for a caller holding the observer role — the gate
    /// admits authorized observability consumers (the eval-dashboard audience), matching
    /// the role-gated session-observability path.
    /// </summary>
    [Fact]
    public async Task GetHistory_AuthenticatedWithObserverRole_Returns200()
    {
        // The factory swaps IMediator for MockMediator; stub the history query so the
        // role-passing request reaches a successful handler result instead of a null
        // Result<T> default. This isolates the assertion to the authorization boundary.
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<GetEvalRunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<EvalRunSummary>>.Success([]));

        using var client = CreateClientAs("observer-user", SessionsController.ObserverRole);

        var response = await client.GetAsync("/api/evals/runs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

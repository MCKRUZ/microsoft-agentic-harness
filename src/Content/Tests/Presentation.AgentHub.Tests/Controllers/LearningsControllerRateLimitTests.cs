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
/// Wire-level rate-limit tests for <c>GET /api/learnings</c>.
///
/// Every recall embeds the query and each candidate learning (1+N uncached embedding calls),
/// so even a role-holding operator can loop the endpoint into real provider spend. The
/// <c>learnings:{user}</c> fixed-window limiter (30/min, mirroring the memory-write limiter's
/// posture) caps that; this test proves the limiter actually fires on the wire rather than
/// only asserting its registration.
/// </summary>
public sealed class LearningsControllerRateLimitTests
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    /// <summary>Initialises the test class with the shared integration factory.</summary>
    public LearningsControllerRateLimitTests(TestWebApplicationFactory factory) =>
        _factory = factory;

    /// <summary>
    /// The 31st request inside one fixed window is rejected with 429 while the first 30
    /// succeed — the per-user window admits exactly the configured budget.
    /// </summary>
    [Fact]
    public async Task Recall_ExceedingPerUserWindow_Returns429()
    {
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<RecallLearningsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<WeightedLearning>>.Success(Array.Empty<WeightedLearning>()));

        using var client = CreateClientAs("rate-limit-user", LearningsController.OperatorRole);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 31; i++)
        {
            using var response = await client.GetAsync("/api/learnings?context=spend-loop");
            statuses.Add(response.StatusCode);
        }

        statuses.Take(30).Should().OnlyContain(s => s == HttpStatusCode.OK,
            "the fixed window admits 30 recalls per minute per user");
        statuses[30].Should().Be(HttpStatusCode.TooManyRequests,
            "the 31st recall in the window must be rejected — unbounded recalls are an embedding-spend loop");
    }

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
}

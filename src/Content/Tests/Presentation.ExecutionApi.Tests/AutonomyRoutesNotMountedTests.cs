using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Proves the autonomy governance API's opt-in gate against the exact real host it exists to
/// protect. The execution API references <c>Presentation.Common</c> and calls <c>AddControllers()</c>,
/// so the Web SDK auto-registers Presentation.Common as an MVC application part and
/// <c>AutonomyController</c> IS discovered here — but the execution API never calls
/// <c>AddAutonomyApi()</c>, so the opt-in action constraint must keep every autonomy route
/// un-matched. The API describes the workload host's governance posture; a bundle host must
/// not accidentally expose it.
/// </summary>
public sealed class AutonomyRoutesNotMountedTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AutonomyRoutesNotMountedTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("GET", "/api/governance/autonomy/tiers/Explore")]
    [InlineData("POST", "/api/governance/autonomy/decision-preview")]
    public async Task AutonomyRoute_InNonOptedHost_Returns404NotAChallenge(string method, string path)
    {
        // 404 specifically — not 401/403. The gate is an action constraint that un-matches the
        // route before authentication, so a probe cannot even learn the routes exist.
        var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a host that never called AddAutonomyApi() must not expose autonomy governance routes");
    }
}

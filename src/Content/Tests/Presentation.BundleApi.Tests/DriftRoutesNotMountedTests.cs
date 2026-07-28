using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Presentation.BundleApi.Tests;

/// <summary>
/// Proves the drift API's opt-in gate against the exact real host it exists to protect.
/// BundleApi references <c>Presentation.Common</c> and calls <c>AddControllers()</c>, so the Web
/// SDK auto-registers Presentation.Common as an MVC application part and
/// <c>DriftController</c> IS discovered here — but BundleApi never calls <c>AddDriftApi()</c>,
/// so the opt-in action constraint must keep every drift route un-matched. Drift state (stores,
/// EWMA, escalation bridge) lives in the workload host; a bundle host must not accidentally
/// expose evaluation-push or baseline-recalculation endpoints.
/// </summary>
public sealed class DriftRoutesNotMountedTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DriftRoutesNotMountedTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("GET", "/api/drift/baselines")]
    [InlineData("GET", "/api/drift/history")]
    [InlineData("GET", "/api/drift/audits")]
    [InlineData("POST", "/api/drift/evaluations")]
    [InlineData("POST", "/api/drift/baselines/00000000-0000-0000-0000-000000000001/recalculate")]
    public async Task DriftRoute_InNonOptedHost_Returns404NotAChallenge(string method, string path)
    {
        // 404 specifically — not 401/403. The gate is an action constraint that un-matches the
        // route before authentication, so a probe cannot even learn the routes exist.
        var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a host that never called AddDriftApi() must not expose drift routes");
    }
}

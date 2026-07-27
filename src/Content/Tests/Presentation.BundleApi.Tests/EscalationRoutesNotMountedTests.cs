using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Presentation.BundleApi.Tests;

/// <summary>
/// Proves the escalation API's opt-in gate against the exact real host it exists to protect.
/// BundleApi references <c>Presentation.Common</c> and calls <c>AddControllers()</c>, so the Web
/// SDK auto-registers Presentation.Common as an MVC application part and
/// <c>EscalationsController</c> IS discovered here — but BundleApi never calls
/// <c>AddEscalationApi()</c>, so the opt-in action constraint must keep every escalation route
/// un-matched. Escalation state lives in the workload host's in-process singleton; a bundle host
/// must not accidentally expose approval endpoints.
/// </summary>
public sealed class EscalationRoutesNotMountedTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EscalationRoutesNotMountedTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("GET", "/api/escalations")]
    [InlineData("GET", "/api/escalations/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/escalations/00000000-0000-0000-0000-000000000001/decision")]
    [InlineData("POST", "/api/escalations/00000000-0000-0000-0000-000000000001/cancel")]
    public async Task EscalationRoute_InNonOptedHost_Returns404NotAChallenge(string method, string path)
    {
        // 404 specifically — not 401/403. The gate is an action constraint that un-matches the
        // route before authentication, so a probe cannot even learn the routes exist.
        var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a host that never called AddEscalationApi() must not expose escalation routes");
    }
}

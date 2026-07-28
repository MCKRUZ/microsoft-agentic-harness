using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Presentation.BundleApi.Tests;

/// <summary>
/// Proves the change-proposal API's opt-in gate against the exact real host it exists to
/// protect. BundleApi references <c>Presentation.Common</c> and calls <c>AddControllers()</c>,
/// so the Web SDK auto-registers Presentation.Common as an MVC application part and
/// <c>ChangeProposalsController</c> IS discovered here — but BundleApi never calls
/// <c>AddChangeProposalApi()</c>, so the opt-in action constraint must keep every
/// change-proposal route un-matched. Proposal state lives in the workload host's in-process
/// store; a bundle host must not accidentally expose decision endpoints.
/// </summary>
public sealed class ChangeProposalRoutesNotMountedTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ChangeProposalRoutesNotMountedTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("GET", "/api/change-proposals")]
    [InlineData("GET", "/api/change-proposals/some-proposal-id")]
    [InlineData("POST", "/api/change-proposals/some-proposal-id/approve")]
    [InlineData("POST", "/api/change-proposals/some-proposal-id/reject")]
    [InlineData("POST", "/api/change-proposals/some-proposal-id/cancel")]
    public async Task ChangeProposalRoute_InNonOptedHost_Returns404NotAChallenge(string method, string path)
    {
        // 404 specifically — not 401/403. The gate is an action constraint that un-matches the
        // route before authentication, so a probe cannot even learn the routes exist.
        var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a host that never called AddChangeProposalApi() must not expose change-proposal routes");
    }
}

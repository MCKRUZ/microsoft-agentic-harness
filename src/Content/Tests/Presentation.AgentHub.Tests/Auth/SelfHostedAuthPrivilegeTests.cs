using FluentAssertions;
using System.Net;
using Xunit;

namespace Presentation.AgentHub.Tests.Auth;

/// <summary>
/// Security-review regression (issue #591): the self-hosted, no-sign-in bypass identity must
/// authenticate every request but hold NO elevated roles — unlike <c>DevAuthHandler</c>, which is
/// appropriate only for a developer on a local machine. Exercises the real, unmodified auth
/// pipeline end to end via <see cref="SelfHostedAuthIntegrationFactory"/> — no <c>TestAuthHandler</c>
/// override — so this proves the actual wiring, not a stand-in for it.
/// </summary>
public sealed class SelfHostedAuthPrivilegeTests : IClassFixture<SelfHostedAuthIntegrationFactory>
{
    private readonly SelfHostedAuthIntegrationFactory _factory;

    public SelfHostedAuthPrivilegeTests(SelfHostedAuthIntegrationFactory factory) => _factory = factory;

    [Fact]
    public async Task OrdinaryAuthorizedEndpoint_Succeeds_WithNoCredentialsSupplied()
    {
        using var client = _factory.CreateClient();

        // ConfigController is [Authorize] with no role requirement — proves the caller IS
        // authenticated (not anonymous, not rejected) despite sending no Authorization header.
        var response = await client.GetAsync("/api/config/deployments");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RoleGatedEndpoint_IsForbidden_ProvingNoElevatedRolesGranted()
    {
        using var client = _factory.CreateClient();

        // DriftController.GetBaselines requires Harness.Drift.Read — the LEAST privileged of the
        // roles DevAuthHandler grants its synthetic principal. If this ever returns 200, the
        // self-hosted bypass has regressed toward DevAuthHandler's privilege level.
        var response = await client.GetAsync("/api/drift/baselines");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

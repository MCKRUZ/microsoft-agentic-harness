using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Presentation.AgentHub.Auth;
using Xunit;

namespace Presentation.AgentHub.Tests.Auth;

/// <summary>
/// Right-to-erasure is refused while sign-in is bypassed (#900). Every caller is then the same synthetic person, so
/// "erase my data" would erase every user's conversations, plans and memories. The end-to-end case runs the real
/// self-hosted bypass pipeline, the way a deployment without an identity provider would.
/// </summary>
public sealed class EraseMyDataUnderBypassTests : IClassFixture<SelfHostedAuthIntegrationFactory>
{
    private readonly SelfHostedAuthIntegrationFactory _factory;

    public EraseMyDataUnderBypassTests(SelfHostedAuthIntegrationFactory factory) => _factory = factory;

    [Fact]
    public async Task EraseMyData_SelfHostedBypass_IsForbiddenBeforeAnythingIsErased()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/compliance/erase-my-data", content: null);

        // The self-hosted caller has a user scope, so a 403 here can only be the shared-identity refusal, not the
        // handler's "no user scope" denial.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("sign-in is bypassed");
    }

    [Theory]
    [InlineData("Dev", true)]
    [InlineData("SelfHosted", true)]
    [InlineData("Bearer", false)]
    [InlineData("AuthenticationTypes.Federation", false)]
    public void IsSharedSyntheticIdentity_RecognisesOnlyTheBypassSchemes(string scheme, bool expected)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "someone")], scheme));

        AuthBypassPolicy.IsSharedSyntheticIdentity(user).Should().Be(expected);
    }

    [Fact]
    public void IsSharedSyntheticIdentity_ABypassIdentityAlongsideARealOne_IsStillShared()
    {
        var user = new ClaimsPrincipal(
        [
            new ClaimsIdentity([new Claim("oid", "real")], "Bearer"),
            new ClaimsIdentity([new Claim("oid", "dev-user")], "Dev"),
        ]);

        AuthBypassPolicy.IsSharedSyntheticIdentity(user).Should().BeTrue();
    }

    [Fact]
    public void IsSharedSyntheticIdentity_AnAnonymousPrincipal_IsNotShared()
    {
        AuthBypassPolicy.IsSharedSyntheticIdentity(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeFalse();
    }
}

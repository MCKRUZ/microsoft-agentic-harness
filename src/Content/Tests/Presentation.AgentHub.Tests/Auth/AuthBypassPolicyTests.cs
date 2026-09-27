using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using Presentation.AgentHub.Auth;
using Xunit;

namespace Presentation.AgentHub.Tests.Auth;

/// <summary>
/// Reproduces issue #591: outside the Development environment, AgentHub required an Entra sign-in
/// with no opt-out, which makes a self-hosted, non-Azure deployment (no Entra tenant to sign into)
/// impossible to reach. These tests pin the exact conditions under which
/// <see cref="AuthBypassPolicy.IsBypassed"/> bypasses sign-in, and — separately — WHICH handler
/// (and therefore which privilege level) each bypass reason selects, so a future edit can't
/// silently widen or narrow either.
/// </summary>
public sealed class AuthBypassPolicyTests
{
    [Theory]
    [InlineData("Development", true, false, true)]
    [InlineData("Development", true, true, true)]
    [InlineData("Development", false, true, false)]
    [InlineData("Production", true, true, true)]
    [InlineData("Production", true, false, false)]
    [InlineData("Production", false, false, false)]
    [InlineData("Container", true, true, true)]
    public void IsBypassed_MatchesExpectedCombination(
        string environmentName, bool disabled, bool allowOutsideDevelopment, bool expected)
    {
        var environment = Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environmentName);
        var configuration = BuildConfiguration(disabled, allowOutsideDevelopment);

        AuthBypassPolicy.IsBypassed(environment, configuration).Should().Be(expected);
    }

    [Fact]
    public void GetBypassSchemeName_Development_SelectsDevHandler_RegardlessOfAllowOutsideDevelopment()
    {
        var environment = Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Development");
        var configuration = BuildConfiguration(disabled: true, allowOutsideDevelopment: true);

        // Development wins when both conditions are true — unchanged local dev behavior takes
        // precedence over a flag that exists for a different deployment shape.
        AuthBypassPolicy.GetBypassSchemeName(environment, configuration).Should().Be(DevAuthHandler.SchemeName);
    }

    [Fact]
    public void GetBypassSchemeName_OutsideDevelopment_SelectsSelfHostedHandler_NotDevHandler()
    {
        var environment = Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Container");
        var configuration = BuildConfiguration(disabled: true, allowOutsideDevelopment: true);

        // Must NOT be DevAuthHandler's scheme — that handler grants escalation/change-proposal-admin
        // and drift/registry-operate roles, which a real, network-reachable deployment with sign-in
        // off must not hand to every unauthenticated caller.
        AuthBypassPolicy.GetBypassSchemeName(environment, configuration).Should().Be(SelfHostedAuthHandler.SchemeName);
    }

    [Fact]
    public void GetBypassSchemeName_NotBypassed_ReturnsNull()
    {
        var environment = Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Production");
        var configuration = BuildConfiguration(disabled: false, allowOutsideDevelopment: false);

        AuthBypassPolicy.GetBypassSchemeName(environment, configuration).Should().BeNull();
    }

    private static IConfiguration BuildConfiguration(bool disabled, bool allowOutsideDevelopment) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Disabled"] = disabled.ToString(),
                ["Auth:AllowOutsideDevelopment"] = allowOutsideDevelopment.ToString(),
            })
            .Build();
}

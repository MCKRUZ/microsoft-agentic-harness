using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.Common.Compliance;
using Xunit;

namespace Presentation.Common.Tests.Compliance;

/// <summary>
/// The shared compliance API (#1003) is served only by a host that mounted it and stated its sign-in bypass schemes. The
/// Web SDK makes this assembly an application part of every MVC host that references it, so without the opt-in a host
/// could serve erasure under a bypass it never declared.
/// </summary>
public sealed class ComplianceApiMountingTests
{
    [Fact]
    public async Task Host_WithApplicationPartButNoOptIn_HasNoComplianceRoutes()
    {
        using var host = await BuildHostAsync(mvc => mvc.AddApplicationPart(typeof(ComplianceController).Assembly));

        var response = await host.GetTestClient().PostAsync("/api/compliance/erase-my-data", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a plain 404 before authentication, so a probe cannot even learn the route exists");
    }

    [Fact]
    public async Task Host_WithAddComplianceApi_ServesTheRoutes_BehindAuthentication()
    {
        using var host = await BuildHostAsync(mvc => mvc.AddComplianceApi([]));

        var response = await host.GetTestClient().PostAsync("/api/compliance/erase-my-data", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void AddComplianceApi_Twice_IsRefused()
    {
        // A second mount could name different bypass schemes and be silently ignored.
        var mvc = new ServiceCollection().AddControllers().AddComplianceApi(["Dev"]);

        var act = () => mvc.AddComplianceApi([]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("Dev", true)]
    [InlineData("ExecutionApiAnonymous", true)]
    [InlineData("Bearer", false)]
    [InlineData("dev", false)]
    public void IsSharedSyntheticIdentity_MatchesOnlyTheHostsSchemes_Exactly(string scheme, bool expected)
    {
        var marker = new ComplianceApiMarker(["Dev", "ExecutionApiAnonymous"]);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "someone")], scheme));

        marker.IsSharedSyntheticIdentity(user).Should().Be(expected);
    }

    [Fact]
    public void IsSharedSyntheticIdentity_ABypassIdentityAlongsideARealOne_IsStillShared()
    {
        var marker = new ComplianceApiMarker(["Dev"]);
        var user = new ClaimsPrincipal(
        [
            new ClaimsIdentity([new Claim("oid", "real")], "Bearer"),
            new ClaimsIdentity([new Claim("oid", "dev-user")], "Dev"),
        ]);

        marker.IsSharedSyntheticIdentity(user).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Marker_ABlankScheme_IsRefused(string scheme)
    {
        var act = () => new ComplianceApiMarker(["Dev", scheme]);

        act.Should().Throw<ArgumentException>();
    }

    private static async Task<IHost> BuildHostAsync(Action<IMvcBuilder> configureMvc) =>
        await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    configureMvc(services.AddControllers());
                    services.AddAuthentication(NoCallerHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, NoCallerHandler>(NoCallerHandler.SchemeName, _ => { });
                    services.AddAuthorization();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }))
            .StartAsync();

    /// <summary>Never authenticates, so a mounted route challenges with 401 and an unmounted one is a 404.</summary>
    private sealed class NoCallerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "ComplianceMountingTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}

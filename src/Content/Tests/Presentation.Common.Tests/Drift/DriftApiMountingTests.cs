using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Application.Core.CQRS.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using FluentAssertions;
using Presentation.Common.Drift;
using Xunit;

namespace Presentation.Common.Tests.Drift;

/// <summary>
/// Proves the drift API's opt-in mounting semantics against real hosts, mirroring the
/// escalation API's gate:
/// <list type="number">
///   <item><description>A host that merely references the assembly (no application part, no
///   marker) has no drift routes.</description></item>
///   <item><description>A host where the assembly IS an application part — which the Web SDK
///   does automatically for any MVC host referencing <c>Presentation.Common</c> — still has no
///   drift routes without the marker: the action constraint un-matches them, yielding a plain
///   404 before authentication. This is the case that makes the opt-in real.</description></item>
///   <item><description>A host that called <c>AddDriftApi()</c> serves the routes.</description></item>
/// </list>
/// </summary>
public sealed class DriftApiMountingTests
{
    [Fact]
    public async Task Host_WithoutPartOrMarker_HasNoDriftRoutes()
    {
        using var host = await BuildHostAsync(mvc => { });

        var response = await host.GetTestClient().GetAsync("/api/drift/baselines");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a host that never mounted the API must not expose its routes");
    }

    [Fact]
    public async Task Host_WithApplicationPartButNoMarker_StillHasNoDriftRoutes()
    {
        // Simulates the Web SDK's automatic ApplicationPartAttribute for referenced MVC
        // assemblies: the controller IS discovered, but the opt-in constraint must keep every
        // route un-matched — a plain 404, not a 401 challenge that would reveal the route.
        using var host = await BuildHostAsync(mvc =>
            mvc.AddApplicationPart(typeof(DriftController).Assembly));

        var response = await host.GetTestClient().GetAsync("/api/drift/baselines");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "auto-discovery of the application part must not arm the routes without AddDriftApi()");
    }

    [Fact]
    public async Task Host_WithAddDriftApi_ServesDriftRoutes()
    {
        using var host = await BuildHostAsync(mvc => mvc.AddDriftApi());

        var response = await host.GetTestClient().GetAsync("/api/drift/baselines");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "AddDriftApi() must arm the routes for an authorized caller");
    }

    [Fact]
    public async Task Host_WithAddDriftApi_UnauthenticatedProbeIsChallengedNotHidden()
    {
        using var host = await BuildHostAsync(mvc => mvc.AddDriftApi(), authenticate: false);

        var response = await host.GetTestClient().GetAsync("/api/drift/baselines");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an armed host enforces authentication on the mounted routes");
    }

    /// <summary>
    /// Builds a minimal MVC host on TestServer with a permissive authentication scheme (read
    /// role + oid identity claim), a stubbed mediator, and the caller-selected MVC configuration.
    /// </summary>
    private static async Task<IHost> BuildHostAsync(
        Action<IMvcBuilder> configureMvc, bool authenticate = true)
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<GetDriftBaselinesQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<DriftBaseline>>.Success([]));

        var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddOptions();
                    services.AddSingleton(mediator.Object);
                    configureMvc(services.AddControllers());
                    var auth = services.AddAuthentication(PermissiveAuthHandler.SchemeName);
                    if (authenticate)
                    {
                        auth.AddScheme<AuthenticationSchemeOptions, PermissiveAuthHandler>(
                            PermissiveAuthHandler.SchemeName, _ => { });
                    }
                    else
                    {
                        auth.AddScheme<AuthenticationSchemeOptions, AnonymousChallengeHandler>(
                            PermissiveAuthHandler.SchemeName, _ => { });
                    }
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

        return host;
    }

    /// <summary>Authenticates every request as an operator holding the read role.</summary>
    private sealed class PermissiveAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "DriftMountingTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
            [
                new Claim("oid", "ops-object-id"),
                new Claim(ClaimTypes.Role, DriftController.ReadRole),
            ], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    /// <summary>Never authenticates, so [Authorize] endpoints challenge with 401.</summary>
    private sealed class AnonymousChallengeHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}

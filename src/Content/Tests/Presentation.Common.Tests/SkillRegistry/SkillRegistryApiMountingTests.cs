using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Application.Core.CQRS.Skills.RefreshSkillRegistry;
using Domain.AI.Skills;
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
using Presentation.Common.SkillRegistry;
using Xunit;

namespace Presentation.Common.Tests.SkillRegistry;

/// <summary>
/// Proves the skill registry API's opt-in mounting semantics against real hosts, mirroring
/// <c>AgentRegistryApiMountingTests</c> from issue #705.
/// <list type="number">
///   <item><description>A host that merely references the assembly has no skill-registry
///   routes.</description></item>
///   <item><description>A host where the assembly IS an application part (the Web SDK's automatic
///   behavior for any MVC host referencing <c>Presentation.Common</c>) still has no routes without
///   the marker — a plain 404 before authentication, the case that makes the opt-in
///   real.</description></item>
///   <item><description>A host that called <c>AddSkillRegistryApi()</c> serves the route, and
///   enforces authentication and the operate role on it.</description></item>
/// </list>
/// </summary>
public sealed class SkillRegistryApiMountingTests
{
    [Fact]
    public async Task Host_WithoutPartOrMarker_HasNoSkillRegistryRoutes()
    {
        using var host = await BuildHostAsync(mvc => { });

        var response = await host.GetTestClient().PostAsync("/api/skill-registry/refresh", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a host that never mounted the API must not expose its routes");
    }

    [Fact]
    public async Task Host_WithApplicationPartButNoMarker_StillHasNoSkillRegistryRoutes()
    {
        using var host = await BuildHostAsync(mvc =>
            mvc.AddApplicationPart(typeof(SkillRegistryController).Assembly));

        var response = await host.GetTestClient().PostAsync("/api/skill-registry/refresh", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "auto-discovery of the application part must not arm the route without AddSkillRegistryApi()");
    }

    [Fact]
    public async Task Host_WithAddSkillRegistryApi_ServesRefreshRoute()
    {
        using var host = await BuildHostAsync(mvc => mvc.AddSkillRegistryApi());

        var response = await host.GetTestClient().PostAsync("/api/skill-registry/refresh", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "AddSkillRegistryApi() must arm the route for an authorized caller");
    }

    [Fact]
    public async Task Host_WithAddSkillRegistryApi_UnauthenticatedProbeIsChallengedNotHidden()
    {
        using var host = await BuildHostAsync(mvc => mvc.AddSkillRegistryApi(), authenticate: false);

        var response = await host.GetTestClient().PostAsync("/api/skill-registry/refresh", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an armed host enforces authentication on the mounted route");
    }

    [Fact]
    public async Task Host_WithAddSkillRegistryApi_AuthenticatedWithoutOperateRole_IsForbidden()
    {
        using var host = await BuildHostAsync(mvc => mvc.AddSkillRegistryApi(), grantOperateRole: false);

        var response = await host.GetTestClient().PostAsync("/api/skill-registry/refresh", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "ordinary skill read access must not be enough to force a refresh");
    }

    /// <summary>
    /// Builds a minimal MVC host on TestServer with a permissive authentication scheme (operate
    /// role + oid identity claim, unless overridden), a stubbed mediator, and the caller-selected
    /// MVC configuration.
    /// </summary>
    private static async Task<IHost> BuildHostAsync(
        Action<IMvcBuilder> configureMvc, bool authenticate = true, bool grantOperateRole = true)
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<RefreshSkillRegistryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<SkillRegistryRefreshResult>.Success(new SkillRegistryRefreshResult
            {
                Added = [],
                Updated = [],
                Removed = [],
                TotalSkillCount = 0,
                SearchedPaths = []
            }));

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
                        services.AddSingleton(new OperateRoleFlag(grantOperateRole));
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

    /// <summary>Distinguishes "grant the operate role" from "authenticate without it" in <see cref="PermissiveAuthHandler"/> — a plain <c>bool</c> cannot be registered directly as a DI singleton.</summary>
    private sealed record OperateRoleFlag(bool Value);

    /// <summary>Authenticates every request as an operator, holding the operate role unless the test opts out.</summary>
    private sealed class PermissiveAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IServiceProvider services) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "SkillRegistryMountingTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var grantOperateRole = services.GetService<OperateRoleFlag>()?.Value ?? true;
            var claims = new List<Claim> { new("oid", "ops-object-id") };
            if (grantOperateRole)
                claims.Add(new Claim(ClaimTypes.Role, SkillRegistryController.OperateRole));

            var identity = new ClaimsIdentity(claims, SchemeName);
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

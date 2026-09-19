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
/// Proves <see cref="SkillRegistryController.Refresh"/>'s caller-identity contract at the wire
/// level, mirroring <c>AgentRegistryControllerIdentityTests</c> from issue #705: the identity
/// recorded against a refresh always comes from the authenticated principal's token, never from
/// anything the caller could supply, and a principal with no usable identity is refused rather than
/// silently attributed to nobody.
/// </summary>
public sealed class SkillRegistryControllerIdentityTests
{
    [Fact]
    public async Task Refresh_AuthenticatedOperator_StampsCallerIdFromToken()
    {
        var (host, mediator) = await BuildHostAsync([new Claim("oid", "ops-object-id")]);
        using var _ = host;
        RefreshSkillRegistryCommand? captured = null;
        SetupRefresh(mediator, c => captured = c);

        var response = await host.GetTestClient().PostAsync("/api/skill-registry/refresh", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().NotBeNull();
        captured!.CallerId.Should().Be("ops-object-id",
            "the identity must come from the authenticated principal's token, not the request");
    }

    [Fact]
    public async Task Refresh_NoIdentityClaim_Returns401NotAnonymousSuccess()
    {
        var (host, mediator) = await BuildHostAsync([]);
        using var _ = host;
        SetupRefresh(mediator, _ => { });

        var response = await host.GetTestClient().PostAsync("/api/skill-registry/refresh", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a refresh that cannot be attributed in the audit trail must not run");
        mediator.Verify(
            m => m.Send(It.IsAny<RefreshSkillRegistryCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static void SetupRefresh(Mock<IMediator> mediator, Action<RefreshSkillRegistryCommand> capture)
    {
        mediator
            .Setup(m => m.Send(It.IsAny<RefreshSkillRegistryCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<SkillRegistryRefreshResult>>, CancellationToken>(
                (c, _) => capture((RefreshSkillRegistryCommand)c))
            .ReturnsAsync(Result<SkillRegistryRefreshResult>.Success(new SkillRegistryRefreshResult
            {
                Added = [],
                Updated = [],
                Removed = [],
                TotalSkillCount = 0,
                SearchedPaths = []
            }));
    }

    /// <summary>
    /// Builds a minimal MVC host with the skill registry API mounted, an authentication scheme
    /// minting the supplied identity claims plus the operate role (an empty claim list means
    /// "authenticated, but with no usable identity claim" — <see cref="AuthenticateResult.NoResult"/>
    /// is reserved for the fully-unauthenticated case already covered by the mounting tests), and a
    /// mockable mediator.
    /// </summary>
    private static async Task<(IHost Host, Mock<IMediator> Mediator)> BuildHostAsync(
        IReadOnlyList<Claim> identityClaims)
    {
        var mediator = new Mock<IMediator>();

        var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddOptions();
                    services.AddSingleton(mediator.Object);
                    services.AddSingleton(identityClaims);
                    services.AddControllers().AddSkillRegistryApi();
                    services.AddAuthentication(ClaimListAuthHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, ClaimListAuthHandler>(
                            ClaimListAuthHandler.SchemeName, _ => { });
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

        return (host, mediator);
    }

    /// <summary>
    /// Authenticates every request with the DI-supplied identity claims plus the operate role,
    /// letting each test shape the principal's identity claims exactly — including an empty list,
    /// which authenticates the request but leaves <c>GetUserIdOrNull</c> with nothing to resolve.
    /// </summary>
    private sealed class ClaimListAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IReadOnlyList<Claim> identityClaims)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "SkillRegistryIdentityTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = identityClaims
                .Append(new Claim(ClaimTypes.Role, SkillRegistryController.OperateRole))
                .ToList();
            var identity = new ClaimsIdentity(claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}

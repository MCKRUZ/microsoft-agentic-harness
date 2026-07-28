using System.Net;
using System.Net.Http.Json;
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
/// Proves the drift write endpoints' caller-identity contract at the wire level:
/// <list type="bullet">
///   <item><description>Union resolution — a production-shaped principal carrying only the JWT
///   inbound-MAPPED objectidentifier URI (never the short <c>oid</c>) still resolves, because
///   resolution searches <c>ApproverClaimTypes.EquivalentFormsOf</c>.</description></item>
///   <item><description>Ambiguity is rejected — two distinct oid values yield 403, never a
///   silent first-pick an attacker could exploit by smuggling a second claim.</description></item>
///   <item><description>The same value under both forms counts as one identity.</description></item>
///   <item><description>A body-supplied caller-id field is ignored: no DTO binds it, the token
///   always wins.</description></item>
/// </list>
/// </summary>
public sealed class DriftControllerIdentityTests
{
    private const string MappedOidUri = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    private static readonly object ValidBody = new
    {
        scope = 1, // DriftScope.Skill (numeric: the minimal host has no string-enum converter)
        scopeIdentifier = "summarize",
        dimensions = new Dictionary<string, double> { ["Faithfulness"] = 0.8 }
    };

    [Fact]
    public async Task PushEvaluation_MappedUriFormClaimOnly_ResolvesViaUnionAndStampsCaller()
    {
        var (host, mediator) = await BuildHostAsync([new Claim(MappedOidUri, "ops-object-id")]);
        using var _ = host;
        PushDriftEvaluationCommand? captured = null;
        SetupPush(mediator, c => captured = c);

        var response = await host.GetTestClient()
            .PostAsJsonAsync("/api/drift/evaluations", ValidBody);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "production tokens carry the inbound-mapped URI form, and it must resolve");
        captured.Should().NotBeNull();
        captured!.CallerId.Should().Be("ops-object-id",
            "the identity must come from the mapped claim form on the token");
    }

    [Fact]
    public async Task PushEvaluation_TwoDistinctOidValues_Returns403FailClosed()
    {
        var (host, mediator) = await BuildHostAsync(
        [
            new Claim("oid", "real-operator"),
            new Claim("oid", "smuggled-operator"),
        ]);
        using var _ = host;
        SetupPush(mediator, _ => { });

        var response = await host.GetTestClient()
            .PostAsJsonAsync("/api/drift/evaluations", ValidBody);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an ambiguous identity is no identity — the attacker must not choose which value wins");
        mediator.Verify(
            m => m.Send(It.IsAny<PushDriftEvaluationCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PushEvaluation_SameValueUnderBothForms_CountsAsOneIdentity()
    {
        var (host, mediator) = await BuildHostAsync(
        [
            new Claim("oid", "ops-object-id"),
            new Claim(MappedOidUri, "ops-object-id"),
        ]);
        using var _ = host;
        PushDriftEvaluationCommand? captured = null;
        SetupPush(mediator, c => captured = c);

        var response = await host.GetTestClient()
            .PostAsJsonAsync("/api/drift/evaluations", ValidBody);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "one value appearing under short and mapped forms is one identity, not an ambiguity");
        captured!.CallerId.Should().Be("ops-object-id");
    }

    [Fact]
    public async Task PushEvaluation_NoIdentityClaim_Returns403FailClosed()
    {
        var (host, mediator) = await BuildHostAsync([]);
        using var _ = host;
        SetupPush(mediator, _ => { });

        var response = await host.GetTestClient()
            .PostAsJsonAsync("/api/drift/evaluations", ValidBody);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a write that cannot be attributed in the audit trail must not run");
    }

    [Fact]
    public async Task PushEvaluation_BodyCallerIdField_IsIgnoredTokenWins()
    {
        var (host, mediator) = await BuildHostAsync([new Claim("oid", "real-operator")]);
        using var _ = host;
        PushDriftEvaluationCommand? captured = null;
        SetupPush(mediator, c => captured = c);

        // The spoofed callerId member has no binding target on the DTO and must be ignored.
        var response = await host.GetTestClient().PostAsJsonAsync("/api/drift/evaluations", new
        {
            scope = 1,
            scopeIdentifier = "summarize",
            dimensions = new Dictionary<string, double> { ["Faithfulness"] = 0.8 },
            callerId = "mallory@evil.example"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().NotBeNull();
        captured!.CallerId.Should().Be("real-operator",
            "a body-supplied caller id must never influence the stamped identity");
    }

    private static void SetupPush(Mock<IMediator> mediator, Action<PushDriftEvaluationCommand> capture)
    {
        mediator
            .Setup(m => m.Send(It.IsAny<PushDriftEvaluationCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<DriftScore>>, CancellationToken>(
                (c, _) => capture((PushDriftEvaluationCommand)c))
            .ReturnsAsync(Result<DriftScore>.Success(new DriftScore
            {
                ScoreId = Guid.NewGuid(),
                BaselineId = Guid.NewGuid(),
                Scope = DriftScope.Skill,
                ScopeIdentifier = "summarize",
                Dimensions = new Dictionary<DriftDimension, DriftDimensionScore>(),
                OverallDrift = 0.0,
                Severity = DriftSeverity.None,
                ScoredAt = DateTimeOffset.UtcNow
            }));
    }

    /// <summary>
    /// Builds a minimal MVC host with the drift API mounted, an authentication scheme minting
    /// the supplied identity claims plus the operate role, and a mockable mediator. The default
    /// <c>AppConfig</c> is used, so the configured caller identity claim type is <c>oid</c>.
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
                    services.AddSingleton<IReadOnlyList<Claim>>(identityClaims);
                    services.AddControllers().AddDriftApi();
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
    /// letting each test shape the principal's identity claims exactly.
    /// </summary>
    private sealed class ClaimListAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IReadOnlyList<Claim> identityClaims)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "DriftIdentityTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = identityClaims
                .Append(new Claim(ClaimTypes.Role, DriftController.OperateRole))
                .ToList();
            var identity = new ClaimsIdentity(claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}

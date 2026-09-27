using System.Net;
using Application.Core.CQRS.Compliance.GenerateComplianceReport;
using Domain.AI.Compliance;
using Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Presentation.AgentHub.Controllers;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Tests for <see cref="ComplianceController"/>'s <c>GET /api/compliance/reports</c> endpoint
/// (#696). This is cross-user, privileged observability data — the same posture as
/// <c>SessionsController</c> — so it is gated with <see cref="SessionsController.ObserverRole"/>
/// rather than the controller's own bare <c>[Authorize]</c>, which only requires authentication.
/// </summary>
/// <remarks>
/// <see cref="TestWebApplicationFactory"/> replaces <see cref="MediatR.IMediator"/> host-wide with
/// <see cref="TestWebApplicationFactory.MockMediator"/> (for the SignalR hub tests sharing this
/// fixture), so every test that reaches the handler dispatch must stub the expected
/// <see cref="GenerateComplianceReportQuery"/> response — mirroring
/// <c>DriftControllerIntegrationTests</c>' established pattern for this same fixture. The
/// authorization test needs no stub: it never reaches the mediator.
/// </remarks>
public sealed class ComplianceControllerReportTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    /// <summary>Initialises the test class with the shared integration factory.</summary>
    public ComplianceControllerReportTests(TestWebApplicationFactory factory) => _factory = factory;

    private static string ReportsUrl()
    {
        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-1);
        return $"/api/compliance/reports?start={start.ToUnixTimeSeconds()}&end={end.ToUnixTimeSeconds()}";
    }

    private static ComplianceReport EmptyReport() => new()
    {
        GeneratedAt = DateTimeOffset.UtcNow,
        GeneratedBy = "observer-user",
        PeriodStart = DateTimeOffset.UtcNow.AddDays(-1),
        PeriodEnd = DateTimeOffset.UtcNow,
        Sessions = new ComplianceSessionSummary
        {
            TotalSessions = 0, CompletedSessions = 0, ErroredSessions = 0, CancelledSessions = 0,
            TotalCostUsd = 0, TotalInputTokens = 0, TotalOutputTokens = 0,
        },
        Safety = new ComplianceSafetySummary
        {
            TotalEvents = 0, BlockedCount = 0, RedactedCount = 0, CountsByCategory = new Dictionary<string, int>(),
        },
        SafetyEvents = [],
        AuditEntries = [],
        GovernanceDecisions = [],
        ChangeDecisions = [],
        EgressDecisions = [],
        EscalationEvents = [],
        DriftFindings = [],
        ChainIntegrity = [],
        Warnings = [],
    };

    /// <summary>Creates an HTTP client authenticated as <paramref name="userId"/> with the supplied roles.</summary>
    private HttpClient CreateClientAs(string userId, params string[] roles)
    {
        var client = _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });
            }))
            .CreateClient();

        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId);
        if (roles.Length > 0)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
        return client;
    }

    /// <summary>
    /// An authenticated caller lacking the observer role is forbidden — a compliance report
    /// exposes cross-user session/safety/audit content, exactly like <c>SessionsController</c>.
    /// Needs no mediator stub: authorization runs before the handler dispatch.
    /// </summary>
    [Fact]
    public async Task GenerateReport_AuthenticatedWithoutObserverRole_Returns403()
    {
        using var client = CreateClientAs("chat-user-no-role");

        var response = await client.GetAsync(ReportsUrl());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>A caller holding the observer role can generate a report.</summary>
    [Fact]
    public async Task GenerateReport_AuthenticatedWithObserverRole_Returns200()
    {
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<GenerateComplianceReportQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ComplianceReport>.Success(EmptyReport()));
        using var client = CreateClientAs("observer-user", SessionsController.ObserverRole);

        var response = await client.GetAsync(ReportsUrl());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>A validation failure from the handler maps to a 400, not a 500.</summary>
    [Fact]
    public async Task GenerateReport_ValidationFailure_Returns400()
    {
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<GenerateComplianceReportQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ComplianceReport>.ValidationFailure(["Start must not be after End."]));
        using var client = CreateClientAs("observer-user", SessionsController.ObserverRole);

        var response = await client.GetAsync(ReportsUrl());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>The caller's identity, not any request field, is what the query carries as CallerId.</summary>
    [Fact]
    public async Task GenerateReport_PassesAuthenticatedIdentityAsCallerId()
    {
        GenerateComplianceReportQuery? captured = null;
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<GenerateComplianceReportQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<ComplianceReport>>, CancellationToken>(
                (q, _) => captured = (GenerateComplianceReportQuery)q)
            .ReturnsAsync(Result<ComplianceReport>.Success(EmptyReport()));
        using var client = CreateClientAs("observer-user-42", SessionsController.ObserverRole);

        await client.GetAsync(ReportsUrl());

        captured.Should().NotBeNull();
        captured!.CallerId.Should().Be("observer-user-42");
    }

    /// <summary>Requesting the Markdown format returns a Markdown content type, not JSON.</summary>
    [Fact]
    public async Task GenerateReport_MarkdownFormat_ReturnsMarkdownContentType()
    {
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<GenerateComplianceReportQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ComplianceReport>.Success(EmptyReport()));
        using var client = CreateClientAs("observer-user", SessionsController.ObserverRole);

        var response = await client.GetAsync(ReportsUrl() + "&format=markdown");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("# Compliance Report");
    }
}

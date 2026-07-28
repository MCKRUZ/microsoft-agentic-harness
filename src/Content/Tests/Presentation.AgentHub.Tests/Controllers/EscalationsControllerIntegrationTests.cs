using System.Net;
using System.Net.Http.Json;
using Application.Core.CQRS.Escalation;
using Domain.AI.Escalation;
using Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Presentation.Common.Escalations;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Wire-level tests for the escalation API mounted into AgentHub via <c>AddEscalationApi()</c>:
/// the routes exist (opt-in wiring works end-to-end through the real host), role gating fails
/// closed, cancel is admin-only, and the approver identity comes from the token's
/// <c>preferred_username</c> claim — a spoofed approver-name field in the request body is
/// ignored because no DTO binds it.
/// </summary>
public sealed class EscalationsControllerIntegrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public EscalationsControllerIntegrationTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetPending_Unauthenticated_Returns401()
    {
        // Also proves the routes are mounted at all: an unmounted route would 404, not challenge.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/escalations");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetPending_AuthenticatedWithoutDecideRole_Returns403()
    {
        using var client = _factory.CreateAuthedClient();

        var response = await client.GetAsync("/api/escalations");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "role gating must fail closed for authenticated callers without Harness.Approvals.Decide");
    }

    [Fact]
    public async Task GetPending_WithDecideRole_Returns200AndStampsIdentityFromToken()
    {
        GetPendingEscalationsForApproverQuery? captured = null;
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<GetPendingEscalationsForApproverQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<IReadOnlyList<EscalationSummary>>>, CancellationToken>(
                (q, _) => captured = (GetPendingEscalationsForApproverQuery)q)
            .ReturnsAsync(Result<IReadOnlyList<EscalationSummary>>.Success([]));

        using var client = _factory.CreateAuthedClient("bob@contoso.com", EscalationsController.DecideRole);

        var response = await client.GetAsync("/api/escalations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().NotBeNull();
        captured!.ApproverName.Should().Be("bob@contoso.com",
            "the approver identity must come from the authenticated principal's claim");
    }

    [Fact]
    public async Task SubmitDecision_BodyApproverNameField_IsIgnoredTokenWins()
    {
        SubmitEscalationDecisionCommand? captured = null;
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<SubmitEscalationDecisionCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<SubmitEscalationDecisionResult>>, CancellationToken>(
                (c, _) => captured = (SubmitEscalationDecisionCommand)c)
            .ReturnsAsync(Result<SubmitEscalationDecisionResult>.Success(
                new SubmitEscalationDecisionResult { Status = EscalationDecisionStatus.DecisionRecorded }));

        using var client = _factory.CreateAuthedClient("alice@contoso.com", EscalationsController.DecideRole);

        // The spoofed approverName member has no binding target on the DTO and must be ignored.
        var response = await client.PostAsJsonAsync(
            $"/api/escalations/{Guid.NewGuid()}/decision",
            new { approve = true, reason = "ok", approverName = "mallory@evil.example" });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "DecisionRecorded maps to 202 per the documented status mapping");
        captured.Should().NotBeNull();
        captured!.ApproverName.Should().Be("alice@contoso.com",
            "a body-supplied approver name must never influence the stamped identity");
        captured.Approve.Should().BeTrue();
        captured.Reason.Should().Be("ok");
    }

    [Fact]
    public async Task Cancel_WithDecideRoleOnly_Returns403()
    {
        using var client = _factory.CreateAuthedClient("alice@contoso.com", EscalationsController.DecideRole);

        var response = await client.PostAsJsonAsync(
            $"/api/escalations/{Guid.NewGuid()}/cancel", new { reason = "stale" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "cancel is admin-only; the decide role must not suffice");
    }

    [Fact]
    public async Task Cancel_WithAdminRole_Returns200()
    {
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<CancelEscalationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<EscalationOutcomeSummary>.Success(new EscalationOutcomeSummary
            {
                EscalationId = Guid.NewGuid(),
                IsApproved = false,
                ResolutionType = EscalationResolutionType.Denied,
                ResolvedAt = DateTimeOffset.UtcNow,
                Decisions = []
            }));

        using var client = _factory.CreateAuthedClient("ops@contoso.com", EscalationsController.AdminRole);

        var response = await client.PostAsJsonAsync(
            $"/api/escalations/{Guid.NewGuid()}/cancel", new { reason = "superseded" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

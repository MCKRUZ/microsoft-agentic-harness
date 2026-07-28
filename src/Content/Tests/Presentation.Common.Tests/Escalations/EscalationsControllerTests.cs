using System.Security.Claims;
using Application.Core.CQRS.Escalation;
using Domain.AI.Escalation;
using Domain.Common;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Presentation.Common.Escalations;
using Xunit;

namespace Presentation.Common.Tests.Escalations;

/// <summary>
/// Direct controller unit tests — no WebApplicationFactory. Verifies that the approver identity
/// is stamped exclusively from the principal's configured claim (the body cannot supply it),
/// that a missing claim fails closed with 403 before any dispatch, and that every
/// <see cref="EscalationDecisionStatus"/> maps to its documented HTTP status
/// (404 / 403 / 202 / 200 / 409) — including an exhaustiveness guard proving no member falls
/// through to a 500. Wire-level auth and routing coverage lives in
/// <c>Presentation.AgentHub.Tests</c>.
/// </summary>
public sealed class EscalationsControllerTests
{
    private const string Approver = "alice@contoso.com";

    private readonly Mock<IMediator> _mediator = new();
    private readonly EscalationConfig _config = new();

    private EscalationsController CreateSut(params Claim[] claims)
    {
        var monitor = new Mock<IOptionsMonitor<EscalationConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(_config);

        var sut = new EscalationsController(
            _mediator.Object, monitor.Object, NullLogger<EscalationsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };
        return sut;
    }

    private static Claim ApproverClaim(string type = "preferred_username", string value = Approver) =>
        new(type, value);

    // --- Identity stamping ---

    [Fact]
    public async Task GetPending_ClaimPresent_StampsApproverFromToken()
    {
        GetPendingEscalationsForApproverQuery? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<GetPendingEscalationsForApproverQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<IReadOnlyList<EscalationSummary>>>, CancellationToken>(
                (q, _) => captured = (GetPendingEscalationsForApproverQuery)q)
            .ReturnsAsync(Result<IReadOnlyList<EscalationSummary>>.Success([]));

        var result = await CreateSut(ApproverClaim()).GetPending(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        captured!.ApproverName.Should().Be(Approver);
    }

    [Fact]
    public async Task GetPending_CustomClaimTypeConfigured_ReadsConfiguredClaimOnly()
    {
        _config.ApproverClaimType = "upn";
        GetPendingEscalationsForApproverQuery? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<GetPendingEscalationsForApproverQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<IReadOnlyList<EscalationSummary>>>, CancellationToken>(
                (q, _) => captured = (GetPendingEscalationsForApproverQuery)q)
            .ReturnsAsync(Result<IReadOnlyList<EscalationSummary>>.Success([]));

        // The principal carries BOTH claims; only the configured one may be used.
        var sut = CreateSut(
            ApproverClaim("preferred_username", "spoof@contoso.com"),
            ApproverClaim("upn", "real@contoso.com"));

        await sut.GetPending(CancellationToken.None);

        captured!.ApproverName.Should().Be("real@contoso.com");
    }

    [Fact]
    public async Task GetPending_MissingConfiguredClaim_Returns403WithGenericDetailAndNoDispatch()
    {
        // Fail-closed: an authenticated, role-holding caller without the configured identity
        // claim cannot be roster-matched and must be rejected before any query is dispatched.
        var sut = CreateSut(new Claim(ClaimTypes.Name, Approver));

        var result = await sut.GetPending(CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var details = problem.Value.Should().BeAssignableTo<ProblemDetails>().Subject;
        details.Detail.Should().NotContain("preferred_username",
            "the response must not teach a caller which claim type to forge; that detail is log-only");
        _mediator.Verify(
            m => m.Send(It.IsAny<GetPendingEscalationsForApproverQuery>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("oid", "http://schemas.microsoft.com/identity/claims/objectidentifier")]
    [InlineData("sub", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")]
    [InlineData("upn", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn")]
    public async Task GetPending_MappedClaimForm_ResolvesConfiguredShortName(
        string configuredType, string mappedType)
    {
        // Production tokens pass through System.IdentityModel.Tokens.Jwt's inbound claim map,
        // which REMAPS these short names to their long-form URIs. Resolution must find the
        // mapped form, or configuring 'oid' (this surface's own production recommendation)
        // would 403 every legitimate approver on the real auth path.
        _config.ApproverClaimType = configuredType;
        GetPendingEscalationsForApproverQuery? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<GetPendingEscalationsForApproverQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<IReadOnlyList<EscalationSummary>>>, CancellationToken>(
                (q, _) => captured = (GetPendingEscalationsForApproverQuery)q)
            .ReturnsAsync(Result<IReadOnlyList<EscalationSummary>>.Success([]));

        var result = await CreateSut(new Claim(mappedType, "mapped-identity"))
            .GetPending(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        captured!.ApproverName.Should().Be("mapped-identity");
    }

    [Fact]
    public async Task GetPending_SameValueUnderShortAndMappedForm_CountsAsOneAndResolves()
    {
        // The same identity arriving under both the short and the mapped form (mixed handler
        // scenarios) is one identity, not an ambiguity.
        _config.ApproverClaimType = "oid";
        _mediator.Setup(m => m.Send(It.IsAny<GetPendingEscalationsForApproverQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<EscalationSummary>>.Success([]));

        var sut = CreateSut(
            new Claim("oid", "user-object-id"),
            new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", "USER-OBJECT-ID"));

        var result = await sut.GetPending(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>(
            "identical values under equivalent forms (case-insensitively) must count as one identity");
    }

    [Fact]
    public async Task GetPending_DifferentValuesAcrossEquivalentForms_Returns403WithoutDispatch()
    {
        // Distinct values under the short and mapped forms are an ambiguous identity — reject,
        // never pick one.
        _config.ApproverClaimType = "oid";
        var sut = CreateSut(
            new Claim("oid", "real-object-id"),
            new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", "smuggled-object-id"));

        var result = await sut.GetPending(CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _mediator.Verify(
            m => m.Send(It.IsAny<GetPendingEscalationsForApproverQuery>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SubmitDecision_ConflictFailure_Returns409()
    {
        // A changed vote comes back from the handler as a Conflict failure and must map to 409
        // through the shared failure mapper.
        _mediator.Setup(m => m.Send(It.IsAny<SubmitEscalationDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<SubmitEscalationDecisionResult>.Conflict(
                "A decision by this approver with the opposite verdict is already recorded; votes cannot be changed."));

        var result = await CreateSut(ApproverClaim()).SubmitDecision(
            Guid.NewGuid(), new SubmitEscalationDecisionRequest(true), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task GetPending_ConfiguredClaimAppearsTwice_Returns403WithoutDispatch()
    {
        // An ambiguous identity is no identity: if the configured claim is present more than
        // once, the controller must reject rather than silently first-pick — an attacker able to
        // smuggle a second instance must not get to choose which value wins.
        var sut = CreateSut(
            ApproverClaim(value: "alice@contoso.com"),
            ApproverClaim(value: "mallory@evil.example"));

        var result = await sut.GetPending(CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _mediator.Verify(
            m => m.Send(It.IsAny<GetPendingEscalationsForApproverQuery>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SubmitDecision_StampsApproverFromTokenNotBody()
    {
        // The wire DTO has no approver-name field at all; this proves the command's identity is
        // exactly the token claim.
        SubmitEscalationDecisionCommand? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<SubmitEscalationDecisionCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<SubmitEscalationDecisionResult>>, CancellationToken>(
                (c, _) => captured = (SubmitEscalationDecisionCommand)c)
            .ReturnsAsync(Result<SubmitEscalationDecisionResult>.Success(new SubmitEscalationDecisionResult
            {
                Status = EscalationDecisionStatus.DecisionRecorded
            }));

        var id = Guid.NewGuid();
        await CreateSut(ApproverClaim()).SubmitDecision(
            id, new SubmitEscalationDecisionRequest(Approve: false, Reason: "risky"), CancellationToken.None);

        captured!.ApproverName.Should().Be(Approver);
        captured.EscalationId.Should().Be(id);
        captured.Approve.Should().BeFalse();
        captured.Reason.Should().Be("risky");
    }

    [Fact]
    public async Task Cancel_StampsCancelledByFromToken()
    {
        CancelEscalationCommand? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<CancelEscalationCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<EscalationOutcomeSummary>>, CancellationToken>(
                (c, _) => captured = (CancelEscalationCommand)c)
            .ReturnsAsync(Result<EscalationOutcomeSummary>.Success(NewOutcomeSummary(Guid.NewGuid())));

        await CreateSut(ApproverClaim()).Cancel(
            Guid.NewGuid(), new CancelEscalationRequest("superseded"), CancellationToken.None);

        captured!.CancelledBy.Should().Be(Approver);
        captured.Reason.Should().Be("superseded");
    }

    // --- Decision status → HTTP mapping (the four documented arms) ---

    [Fact]
    public async Task SubmitDecision_UnknownEscalation_Returns404()
    {
        SetupDecision(new SubmitEscalationDecisionResult { Status = EscalationDecisionStatus.UnknownEscalation });

        var result = await CreateSut(ApproverClaim()).SubmitDecision(
            Guid.NewGuid(), new SubmitEscalationDecisionRequest(true), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task SubmitDecision_ApproverNotAuthorized_Returns403()
    {
        SetupDecision(new SubmitEscalationDecisionResult { Status = EscalationDecisionStatus.ApproverNotAuthorized });

        var result = await CreateSut(ApproverClaim()).SubmitDecision(
            Guid.NewGuid(), new SubmitEscalationDecisionRequest(true), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task SubmitDecision_DecisionRecorded_Returns202()
    {
        SetupDecision(new SubmitEscalationDecisionResult { Status = EscalationDecisionStatus.DecisionRecorded });

        var result = await CreateSut(ApproverClaim()).SubmitDecision(
            Guid.NewGuid(), new SubmitEscalationDecisionRequest(true), CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        var body = accepted.Value.Should().BeOfType<EscalationDecisionResponse>().Subject;
        body.Status.Should().Be(EscalationDecisionStatus.DecisionRecorded);
        body.Outcome.Should().BeNull();
    }

    [Fact]
    public async Task SubmitDecision_Resolved_Returns200WithOutcome()
    {
        var id = Guid.NewGuid();
        SetupDecision(new SubmitEscalationDecisionResult
        {
            Status = EscalationDecisionStatus.Resolved,
            Outcome = NewOutcomeSummary(id)
        });

        var result = await CreateSut(ApproverClaim()).SubmitDecision(
            id, new SubmitEscalationDecisionRequest(true), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<EscalationDecisionResponse>().Subject;
        body.Status.Should().Be(EscalationDecisionStatus.Resolved);
        body.Outcome!.EscalationId.Should().Be(id);
    }

    [Fact]
    public async Task SubmitDecision_AwaitingReconciliation_Returns409NotServerError()
    {
        // Reachable in the DEFAULT durability-off config: approver A's decision resolves the
        // escalation, the fail-closed audit write throws, the escalation parks with
        // ResolutionFailed set and stays in the active set, and approver B's decision then comes
        // back AwaitingReconciliation. With no arm for it the switch fell through to the 500
        // default, so an ordinary lifecycle state read to the caller as a server fault.
        SetupDecision(new SubmitEscalationDecisionResult
        {
            Status = EscalationDecisionStatus.AwaitingReconciliation
        });

        var result = await CreateSut(ApproverClaim()).SubmitDecision(
            Guid.NewGuid(), new SubmitEscalationDecisionRequest(true), CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict,
            "the verdict is already decided, so this vote will never be counted no matter how " +
            "often the request is retried — a state conflict, not the transient unavailability 503 implies");

        var details = problem.Value.Should().BeAssignableTo<ProblemDetails>().Subject;
        details.Detail.Should().Contain("not counted",
            "the approver must be told plainly that their vote did not participate");
        details.Detail.Should().Contain("GET /api/escalations",
            "the caller needs the poll target for the verdict that reconciliation will publish");
    }

    [Fact]
    public async Task SubmitDecision_ConflictingDecision_Returns409NotServerError()
    {
        // The handler normally translates this to a Conflict failure, so this status should not
        // arrive here. It is mapped anyway: a handler change must not be able to silently demote
        // a votes-cannot-be-changed conflict into a 500.
        SetupDecision(new SubmitEscalationDecisionResult
        {
            Status = EscalationDecisionStatus.ConflictingDecision
        });

        var result = await CreateSut(ApproverClaim()).SubmitDecision(
            Guid.NewGuid(), new SubmitEscalationDecisionRequest(true), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task SubmitDecision_EveryDecisionStatus_MapsToSomethingOtherThan500()
    {
        // The guard for the whole defect class: adding a member to EscalationDecisionStatus
        // without an arm in the controller silently turns it into a 500. This fails the moment
        // that happens, naming the unmapped member.
        foreach (var status in Enum.GetValues<EscalationDecisionStatus>())
        {
            _mediator.Reset();
            SetupDecision(new SubmitEscalationDecisionResult
            {
                Status = status,
                Outcome = status == EscalationDecisionStatus.Resolved
                    ? NewOutcomeSummary(Guid.NewGuid())
                    : null
            });

            var result = await CreateSut(ApproverClaim()).SubmitDecision(
                Guid.NewGuid(), new SubmitEscalationDecisionRequest(true), CancellationToken.None);

            var statusCode = result switch
            {
                ObjectResult o => o.StatusCode,
                StatusCodeResult s => s.StatusCode,
                _ => null
            };
            statusCode.Should().NotBe(StatusCodes.Status500InternalServerError,
                $"EscalationDecisionStatus.{status} has no explicit arm in the controller's mapping");
        }
    }

    // --- Read/cancel Result mapping through the shared failure mapper ---

    [Fact]
    public async Task GetById_NotFoundResult_Returns404()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetEscalationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<EscalationDetail>.NotFound("No escalation with the given id is visible to the caller."));

        var result = await CreateSut(ApproverClaim()).GetById(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetById_Success_ReturnsOkWithDetail()
    {
        var detail = EscalationDetail.ForResolved(NewOutcomeSummary(Guid.NewGuid()));
        _mediator.Setup(m => m.Send(It.IsAny<GetEscalationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<EscalationDetail>.Success(detail));

        var result = await CreateSut(ApproverClaim()).GetById(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(detail);
    }

    [Fact]
    public async Task Cancel_ConflictResult_Returns409()
    {
        _mediator.Setup(m => m.Send(It.IsAny<CancelEscalationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<EscalationOutcomeSummary>.Conflict("The escalation is already resolved."));

        var result = await CreateSut(ApproverClaim()).Cancel(
            Guid.NewGuid(), new CancelEscalationRequest("stale"), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    // --- Authorization metadata (role gating shape; wire enforcement tested in AgentHub) ---

    [Fact]
    public void Controller_ListGetDecide_RequireDecideRole_CancelRequiresAdminRole()
    {
        static string? RolesOf(string method) =>
            typeof(EscalationsController).GetMethod(method)!
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
                .Cast<AuthorizeAttribute>().Single().Roles;

        RolesOf(nameof(EscalationsController.GetPending)).Should().Be(EscalationsController.DecideRole);
        RolesOf(nameof(EscalationsController.GetById)).Should().Be(EscalationsController.DecideRole);
        RolesOf(nameof(EscalationsController.SubmitDecision)).Should().Be(EscalationsController.DecideRole);
        RolesOf(nameof(EscalationsController.Cancel)).Should().Be(EscalationsController.AdminRole,
            "cancellation is an operator power, gated separately from approving");
    }

    [Fact]
    public void Controller_CarriesOptInConstraint()
    {
        typeof(EscalationsController)
            .GetCustomAttributes(typeof(RequiresEscalationApiOptInAttribute), inherit: false)
            .Should().HaveCount(1, "routes must be un-matched in hosts that did not call AddEscalationApi");
    }

    private void SetupDecision(SubmitEscalationDecisionResult value) =>
        _mediator.Setup(m => m.Send(It.IsAny<SubmitEscalationDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<SubmitEscalationDecisionResult>.Success(value));

    private static EscalationOutcomeSummary NewOutcomeSummary(Guid id) => new()
    {
        EscalationId = id,
        IsApproved = false,
        ResolutionType = EscalationResolutionType.Denied,
        ResolvedAt = DateTimeOffset.UtcNow,
        Decisions = []
    };
}

using System.Security.Claims;
using Application.AI.Common.CQRS.Changes.ApproveChangeProposal;
using Application.AI.Common.CQRS.Changes.CancelChangeProposal;
using Application.AI.Common.CQRS.Changes.GetChangeProposal;
using Application.AI.Common.CQRS.Changes.ListChangeProposals;
using Application.AI.Common.CQRS.Changes.RejectChangeProposal;
using Domain.AI.Changes;
using Domain.AI.Identity;
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
using Presentation.Common.ChangeProposals;
using Xunit;
using EditOp = Domain.AI.SkillTraining.EditOp;

namespace Presentation.Common.Tests.ChangeProposals;

/// <summary>
/// Direct controller unit tests — no WebApplicationFactory. Verifies that the reviewer identity
/// is stamped exclusively from the principal's configured claim (the body cannot supply it —
/// no request DTO carries a reviewer field), that a missing or ambiguous claim fails closed
/// with 403 before any dispatch, that claim resolution searches the JWT inbound-mapped
/// equivalent forms, and that Result failures map through the shared failure mapper
/// (409 double-decide, 404 unknown id). Wire-level auth and routing coverage lives in
/// <c>Presentation.AgentHub.Tests</c>.
/// </summary>
public sealed class ChangeProposalsControllerTests
{
    private const string Reviewer = "alice@contoso.com";

    private readonly Mock<IMediator> _mediator = new();
    private readonly EscalationConfig _config = new();

    private ChangeProposalsController CreateSut(params Claim[] claims)
    {
        var monitor = new Mock<IOptionsMonitor<EscalationConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(_config);

        return new ChangeProposalsController(
            _mediator.Object, monitor.Object, NullLogger<ChangeProposalsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };
    }

    private static Claim ReviewerClaim(string type = "preferred_username", string value = Reviewer) =>
        new(type, value);

    private static ChangeProposal NewProposal(ChangeProposalStatus status = ChangeProposalStatus.AwaitingApproval) =>
        ChangeProposal.Create(
            target: new GitRepoTarget("https://github.com/org/repo", "main", "abc123"),
            diff: [new ChangeEdit { Op = EditOp.Replace, Target = "foo", Content = "bar" }],
            submittedBy: new AgentIdentity { Id = "agent-001", Kind = AgentIdentityKind.ManagedIdentity },
            summary: "rename foo to bar",
            blastRadius: BlastRadius.Low,
            requiredGates: ["self_validation", "approval", "merge"],
            submittedAt: new DateTimeOffset(2026, 6, 6, 10, 30, 15, TimeSpan.Zero)) with
        {
            Status = status
        };

    // --- Identity stamping ---

    [Fact]
    public async Task Approve_ClaimPresent_StampsReviewerFromToken()
    {
        ApproveChangeProposalCommand? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<ApproveChangeProposalCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<ChangeProposal>>, CancellationToken>(
                (c, _) => captured = (ApproveChangeProposalCommand)c)
            .ReturnsAsync(Result<ChangeProposal>.Success(NewProposal(ChangeProposalStatus.Approved)));

        var result = await CreateSut(ReviewerClaim()).Approve(
            "proposal-1", new ApproveChangeProposalRequest("looks good"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        captured.Should().NotBeNull();
        captured!.ReviewerId.Should().Be(Reviewer,
            "the reviewer identity must come from the authenticated principal's claim");
        captured.ProposalId.Should().Be("proposal-1");
        captured.Reason.Should().Be("looks good");
    }

    [Fact]
    public async Task Reject_ClaimPresent_StampsReviewerFromToken()
    {
        RejectChangeProposalCommand? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<RejectChangeProposalCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<ChangeProposal>>, CancellationToken>(
                (c, _) => captured = (RejectChangeProposalCommand)c)
            .ReturnsAsync(Result<ChangeProposal>.Success(NewProposal(ChangeProposalStatus.Rejected)));

        await CreateSut(ReviewerClaim()).Reject(
            "proposal-1", new RejectChangeProposalRequest("too risky"), CancellationToken.None);

        captured!.ReviewerId.Should().Be(Reviewer);
        captured.Reason.Should().Be("too risky");
    }

    [Fact]
    public async Task Cancel_ClaimPresent_StampsCancelledByFromToken()
    {
        CancelChangeProposalCommand? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<CancelChangeProposalCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<ChangeProposal>>, CancellationToken>(
                (c, _) => captured = (CancelChangeProposalCommand)c)
            .ReturnsAsync(Result<ChangeProposal>.Success(NewProposal(ChangeProposalStatus.Cancelled)));

        await CreateSut(ReviewerClaim()).Cancel(
            "proposal-1", new CancelChangeProposalRequest("superseded"), CancellationToken.None);

        captured!.CancelledBy.Should().Be(Reviewer);
        captured.Reason.Should().Be("superseded");
    }

    [Fact]
    public async Task Approve_MissingConfiguredClaim_Returns403WithGenericDetailAndNoDispatch()
    {
        // Fail-closed: an authenticated, role-holding caller without the configured identity
        // claim cannot be attributed in the audit history and must be rejected before dispatch.
        var sut = CreateSut(new Claim(ClaimTypes.Name, Reviewer));

        var result = await sut.Approve(
            "proposal-1", new ApproveChangeProposalRequest(), CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var details = problem.Value.Should().BeAssignableTo<ProblemDetails>().Subject;
        details.Detail.Should().NotContain("preferred_username",
            "the response must not teach a caller which claim type to forge; that detail is log-only");
        _mediator.Verify(
            m => m.Send(It.IsAny<ApproveChangeProposalCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // --- Claim-union resolution (JWT inbound-mapped forms) ---

    [Theory]
    [InlineData("oid", "http://schemas.microsoft.com/identity/claims/objectidentifier")]
    [InlineData("sub", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")]
    [InlineData("upn", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn")]
    public async Task Approve_MappedClaimForm_ResolvesConfiguredShortName(
        string configuredType, string mappedType)
    {
        // Production tokens pass through System.IdentityModel.Tokens.Jwt's inbound claim map,
        // which REMAPS these short names to their long-form URIs. Resolution must find the
        // mapped form, or configuring 'oid' would 403 every legitimate reviewer on the real
        // auth path.
        _config.ApproverClaimType = configuredType;
        ApproveChangeProposalCommand? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<ApproveChangeProposalCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<ChangeProposal>>, CancellationToken>(
                (c, _) => captured = (ApproveChangeProposalCommand)c)
            .ReturnsAsync(Result<ChangeProposal>.Success(NewProposal(ChangeProposalStatus.Approved)));

        var result = await CreateSut(new Claim(mappedType, "mapped-identity")).Approve(
            "proposal-1", new ApproveChangeProposalRequest(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        captured!.ReviewerId.Should().Be("mapped-identity");
    }

    [Fact]
    public async Task Approve_SameValueUnderShortAndMappedForm_CountsAsOneAndResolves()
    {
        // The same identity arriving under both the short and the mapped form (mixed handler
        // scenarios) is one identity, not an ambiguity.
        _config.ApproverClaimType = "oid";
        _mediator.Setup(m => m.Send(It.IsAny<ApproveChangeProposalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ChangeProposal>.Success(NewProposal(ChangeProposalStatus.Approved)));

        var sut = CreateSut(
            new Claim("oid", "user-object-id"),
            new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", "USER-OBJECT-ID"));

        var result = await sut.Approve(
            "proposal-1", new ApproveChangeProposalRequest(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>(
            "identical values under equivalent forms (case-insensitively) must count as one identity");
    }

    [Fact]
    public async Task Approve_DifferentValuesAcrossEquivalentForms_Returns403WithoutDispatch()
    {
        // Distinct values under the short and mapped forms are an ambiguous identity — reject,
        // never pick one.
        _config.ApproverClaimType = "oid";
        var sut = CreateSut(
            new Claim("oid", "real-object-id"),
            new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", "smuggled-object-id"));

        var result = await sut.Approve(
            "proposal-1", new ApproveChangeProposalRequest(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _mediator.Verify(
            m => m.Send(It.IsAny<ApproveChangeProposalCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Approve_ConfiguredClaimAppearsTwice_Returns403WithoutDispatch()
    {
        // An ambiguous identity is no identity: an attacker able to smuggle a second instance of
        // the claim must not get to choose which value is written into the audit history.
        var sut = CreateSut(
            ReviewerClaim(value: "alice@contoso.com"),
            ReviewerClaim(value: "mallory@evil.example"));

        var result = await sut.Approve(
            "proposal-1", new ApproveChangeProposalRequest(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _mediator.Verify(
            m => m.Send(It.IsAny<ApproveChangeProposalCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // --- Result failure mapping through the shared failure mapper ---

    [Fact]
    public async Task Approve_ConflictResult_Returns409()
    {
        // Deciding an already-decided proposal comes back from the handler as a Conflict
        // failure and must map to 409 through the shared failure mapper.
        _mediator.Setup(m => m.Send(It.IsAny<ApproveChangeProposalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ChangeProposal>.Conflict(
                "Cannot approve proposal in status Merged (must be AwaitingApproval)."));

        var result = await CreateSut(ReviewerClaim()).Approve(
            "proposal-1", new ApproveChangeProposalRequest(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Approve_NotFoundResult_Returns404()
    {
        _mediator.Setup(m => m.Send(It.IsAny<ApproveChangeProposalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ChangeProposal>.NotFound("The requested change proposal was not found."));

        var result = await CreateSut(ReviewerClaim()).Approve(
            "nope", new ApproveChangeProposalRequest(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Approve_ForbiddenResult_Returns403()
    {
        // The handler returns Forbidden when the change-proposal pipeline is disabled.
        _mediator.Setup(m => m.Send(It.IsAny<ApproveChangeProposalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ChangeProposal>.Forbidden("ChangeProposal pipeline is disabled."));

        var result = await CreateSut(ReviewerClaim()).Approve(
            "proposal-1", new ApproveChangeProposalRequest(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task GetById_NotFoundResult_Returns404()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetChangeProposalQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ChangeProposal>.NotFound("The requested change proposal was not found."));

        var result = await CreateSut(ReviewerClaim()).GetById("nope", CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    // --- Read projections ---

    [Fact]
    public async Task GetById_Success_ReturnsProjectedDetail()
    {
        var proposal = NewProposal();
        _mediator.Setup(m => m.Send(It.IsAny<GetChangeProposalQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ChangeProposal>.Success(proposal));

        var result = await CreateSut(ReviewerClaim()).GetById(proposal.Id, CancellationToken.None);

        var detail = result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ChangeProposalDetailResponse>().Subject;
        detail.Id.Should().Be(proposal.Id);
        detail.Status.Should().Be(ChangeProposalStatus.AwaitingApproval);
        detail.TargetKind.Should().Be(ChangeTargetKind.GitRepo);
        detail.TargetDisplayName.Should().Be(proposal.Target.DisplayName);
        detail.SubmittedByAgentId.Should().Be("agent-001");
        detail.Diff.Should().BeEquivalentTo(proposal.Diff);
        detail.History.Should().BeEquivalentTo(proposal.History);
    }

    [Fact]
    public async Task GetProposals_PassesStatusFilterAndProjectsSummaries()
    {
        ListChangeProposalsQuery? captured = null;
        var proposal = NewProposal();
        _mediator.Setup(m => m.Send(It.IsAny<ListChangeProposalsQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<IReadOnlyList<ChangeProposal>>>, CancellationToken>(
                (q, _) => captured = (ListChangeProposalsQuery)q)
            .ReturnsAsync(Result<IReadOnlyList<ChangeProposal>>.Success([proposal]));

        var result = await CreateSut(ReviewerClaim()).GetProposals(
            ChangeProposalStatus.AwaitingApproval, CancellationToken.None);

        captured!.Filter.Status.Should().Be(ChangeProposalStatus.AwaitingApproval);
        var summaries = result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<IReadOnlyList<ChangeProposalSummaryResponse>>().Subject;
        summaries.Should().ContainSingle()
            .Which.Id.Should().Be(proposal.Id);
    }

    // --- Authorization metadata (role gating shape; wire enforcement tested in AgentHub) ---

    [Fact]
    public void Controller_ListGetApproveReject_RequireDecideRole_CancelRequiresAdminRole()
    {
        static string? RolesOf(string method) =>
            typeof(ChangeProposalsController).GetMethod(method)!
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
                .Cast<AuthorizeAttribute>().Single().Roles;

        RolesOf(nameof(ChangeProposalsController.GetProposals)).Should().Be(ChangeProposalsController.DecideRole);
        RolesOf(nameof(ChangeProposalsController.GetById)).Should().Be(ChangeProposalsController.DecideRole);
        RolesOf(nameof(ChangeProposalsController.Approve)).Should().Be(ChangeProposalsController.DecideRole);
        RolesOf(nameof(ChangeProposalsController.Reject)).Should().Be(ChangeProposalsController.DecideRole);
        RolesOf(nameof(ChangeProposalsController.Cancel)).Should().Be(ChangeProposalsController.AdminRole,
            "cancellation is an operator power, gated separately from deciding");
    }

    [Fact]
    public void Controller_CarriesOptInConstraint()
    {
        typeof(ChangeProposalsController)
            .GetCustomAttributes(typeof(RequiresChangeProposalApiOptInAttribute), inherit: false)
            .Should().HaveCount(1, "routes must be un-matched in hosts that did not call AddChangeProposalApi");
    }
}

using Application.AI.Common.CQRS.Changes.ApproveChangeProposal;
using Application.AI.Common.CQRS.Changes.CancelChangeProposal;
using Application.AI.Common.CQRS.Changes.GetChangeProposal;
using Application.AI.Common.CQRS.Changes.ListChangeProposals;
using Application.AI.Common.CQRS.Changes.RejectChangeProposal;
using Application.AI.Common.Interfaces.Changes;
using Application.Core.Validation;
using Domain.AI.Changes;
using Domain.AI.Escalation;
using Domain.Common;
using Domain.Common.Config.AI.Governance;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.Common.Extensions;

namespace Presentation.Common.ChangeProposals;

/// <summary>
/// REST API for deciding change proposals — the human side of the change-proposal approval
/// gate. Reviewers list and inspect proposals and submit approve/reject decisions;
/// administrators cancel proposals that should not proceed. An approval immediately enqueues
/// the proposal for the in-process merge worker.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in mount.</b> The controller ships in <c>Presentation.Common</c> but its routes only
/// exist in hosts that called
/// <see cref="ChangeProposalApiMvcBuilderExtensions.AddChangeProposalApi"/> — see
/// <see cref="RequiresChangeProposalApiOptInAttribute"/>. Proposal state is an in-process store,
/// so the API must be co-resident with the pipeline that owns it.
/// </para>
/// <para>
/// <b>Authorization model — honest limitation.</b> Change proposals carry no per-proposal
/// reviewer roster (unlike escalations): the <see cref="DecideRole"/> app role is the
/// <em>entire</em> authorization for approve/reject, and any role holder may decide any
/// proposal. The reviewer identity resolved from the token is stamped into the proposal's
/// gate-history audit entry (<see cref="GateDecision.ReviewerId"/>) — it is accountability
/// metadata, not an authorization input. Consumers needing per-proposal reviewer scoping should
/// route proposals through the escalation system (<c>EscalationServiceApprovalRouter</c>),
/// which does enforce rosters.
/// </para>
/// <para>
/// <b>Identity from token, never from body.</b> The reviewer identity is resolved from the
/// authenticated principal's claims (claim type shared with the escalation API via
/// <c>EscalationConfig.ApproverClaimType</c>, searched across its JWT inbound-mapped equivalent
/// forms). No request DTO carries a reviewer-id field, so a caller cannot record a decision as
/// someone else. A principal lacking the configured claim — or carrying more than one distinct
/// value (an ambiguous identity is no identity) — is rejected with a generic 403 (fail-closed).
/// </para>
/// <para>
/// <b>Existence disclosure.</b> Proposals have no roster to scope reads, so <c>GET /{id}</c>
/// returns any proposal to any <see cref="DecideRole"/> holder; an unknown id is a plain 404
/// and decision endpoints never reveal more about an id than that same 404.
/// </para>
/// </remarks>
[ApiController]
[Route("api/change-proposals")]
[RequiresChangeProposalApiOptIn]
public sealed class ChangeProposalsController : ControllerBase
{
    /// <summary>
    /// App role required to list, read, approve, and reject change proposals. Follows the
    /// <c>Harness.Approvals.Decide</c> precedent of role-gating privileged decision surfaces.
    /// Because proposals carry no per-item reviewer roster, this role is the entire
    /// authorization for approve/reject — grant it accordingly.
    /// </summary>
    public const string DecideRole = "Harness.Proposals.Decide";

    /// <summary>
    /// App role required to cancel a change proposal. Held separately from
    /// <see cref="DecideRole"/>: cancellation withdraws a change regardless of where it sits in
    /// the gate pipeline, which is an operator power, not a reviewer power.
    /// </summary>
    public const string AdminRole = "Harness.Proposals.Admin";

    private readonly IMediator _mediator;
    private readonly IOptionsMonitor<EscalationConfig> _config;
    private readonly ILogger<ChangeProposalsController> _logger;

    /// <summary>Initializes the controller with its dependencies.</summary>
    /// <param name="mediator">The MediatR mediator used to dispatch change-proposal operations.</param>
    /// <param name="config">Escalation configuration; supplies the shared reviewer identity claim type.</param>
    /// <param name="logger">Logger for identity-resolution diagnostics (claim details never leave the trust boundary in responses).</param>
    public ChangeProposalsController(
        IMediator mediator,
        IOptionsMonitor<EscalationConfig> config,
        ILogger<ChangeProposalsController> logger)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _mediator = mediator;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Lists change proposals, optionally filtered to one lifecycle status (most usefully
    /// <c>AwaitingApproval</c> — the reviewer work queue).
    /// </summary>
    /// <param name="status">Optional status filter; omitted matches every status.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching proposal summaries (possibly empty).</returns>
    /// <response code="200">The matching proposals (may contain zero results).</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks <see cref="DecideRole"/>.</response>
    [HttpGet]
    [Authorize(Roles = DecideRole)]
    [ProducesResponseType(typeof(IReadOnlyList<ChangeProposalSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetProposals(
        [FromQuery] ChangeProposalStatus? status,
        CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new ListChangeProposalsQuery { Filter = new ChangeProposalQuery { Status = status } },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(result.Value!.Select(ChangeProposalSummaryResponse.From).ToList())
            : MapFailure(result);
    }

    /// <summary>
    /// Reads one change proposal in full — summary, diff, required gates, and the gate-decision
    /// audit history. This is the review target before a decision and the poll target after one.
    /// </summary>
    /// <param name="id">The proposal id (Base64URL-encoded deterministic hash).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The full proposal detail.</returns>
    /// <response code="200">The proposal detail.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks <see cref="DecideRole"/>.</response>
    /// <response code="404">No proposal with the given id.</response>
    [HttpGet("{id}")]
    [Authorize(Roles = DecideRole)]
    [ProducesResponseType(typeof(ChangeProposalDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(string id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new GetChangeProposalQuery { Id = id },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(ChangeProposalDetailResponse.From(result.Value!))
            : MapFailure(result);
    }

    /// <summary>
    /// Approves a proposal currently awaiting approval. The reviewer identity is taken from the
    /// token, never from the body, and is recorded in the gate-history audit entry. On success
    /// the proposal is enqueued for the in-process merge worker; poll <c>GET /{id}</c> for the
    /// final Merged/Rejected outcome.
    /// </summary>
    /// <param name="id">The proposal id.</param>
    /// <param name="request">The optional approval reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Approved proposal snapshot (merge continues out-of-band).</returns>
    /// <response code="200">Approval recorded; the body carries the Approved snapshot.</response>
    /// <response code="400">Invalid request (e.g. oversized reason).</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks <see cref="DecideRole"/>, has no usable reviewer identity claim, or the change-proposal pipeline is disabled.</response>
    /// <response code="404">No proposal with the given id.</response>
    /// <response code="409">The proposal is not awaiting approval (already decided, merging, or terminal).</response>
    [HttpPost("{id}/approve")]
    [Authorize(Roles = DecideRole)]
    [ProducesResponseType(typeof(ChangeProposalDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(
        string id,
        [FromBody] ApproveChangeProposalRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolveReviewerId() is not { } reviewerId)
            return MissingReviewerClaim();

        var result = await _mediator.Send(new ApproveChangeProposalCommand
        {
            ProposalId = id,
            ReviewerId = reviewerId,
            Reason = request.Reason ?? string.Empty
        }, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(ChangeProposalDetailResponse.From(result.Value!))
            : MapFailure(result);
    }

    /// <summary>
    /// Rejects a proposal currently awaiting approval, driving it to terminal Rejected. The
    /// reviewer identity is taken from the token, never from the body; the required reason
    /// surfaces in the audit trail and back to the submitting agent.
    /// </summary>
    /// <param name="id">The proposal id.</param>
    /// <param name="request">The required rejection reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Rejected proposal snapshot.</returns>
    /// <response code="200">Rejection recorded; the body carries the Rejected snapshot.</response>
    /// <response code="400">Missing or oversized reason.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks <see cref="DecideRole"/> or has no usable reviewer identity claim.</response>
    /// <response code="404">No proposal with the given id.</response>
    /// <response code="409">The proposal is not awaiting approval (already decided, merging, or terminal).</response>
    [HttpPost("{id}/reject")]
    [Authorize(Roles = DecideRole)]
    [ProducesResponseType(typeof(ChangeProposalDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reject(
        string id,
        [FromBody] RejectChangeProposalRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolveReviewerId() is not { } reviewerId)
            return MissingReviewerClaim();

        var result = await _mediator.Send(new RejectChangeProposalCommand
        {
            ProposalId = id,
            ReviewerId = reviewerId,
            Reason = request.Reason
        }, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(ChangeProposalDetailResponse.From(result.Value!))
            : MapFailure(result);
    }

    /// <summary>
    /// Administratively cancels a proposal before its merge starts, driving it to terminal
    /// Cancelled. Distinct from rejection: no gate produced an adverse decision, the change was
    /// withdrawn. The cancelling identity is taken from the token and recorded in the audit
    /// history.
    /// </summary>
    /// <param name="id">The proposal id.</param>
    /// <param name="request">The optional cancellation reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Cancelled proposal snapshot.</returns>
    /// <response code="200">Cancellation recorded; the body carries the Cancelled snapshot.</response>
    /// <response code="400">Invalid request (e.g. oversized reason).</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks <see cref="AdminRole"/> or has no usable reviewer identity claim.</response>
    /// <response code="404">No proposal with the given id.</response>
    /// <response code="409">The proposal is already terminal, or its merge is in progress and can no longer be cancelled.</response>
    [HttpPost("{id}/cancel")]
    [Authorize(Roles = AdminRole)]
    [ProducesResponseType(typeof(ChangeProposalDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(
        string id,
        [FromBody] CancelChangeProposalRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolveReviewerId() is not { } cancelledBy)
            return MissingReviewerClaim();

        var result = await _mediator.Send(new CancelChangeProposalCommand
        {
            ProposalId = id,
            CancelledBy = cancelledBy,
            Reason = request.Reason ?? string.Empty
        }, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(ChangeProposalDetailResponse.From(result.Value!))
            : MapFailure(result);
    }

    /// <summary>
    /// Resolves the caller's reviewer identity from the configured claim type, or null when the
    /// principal does not carry exactly one usable value. Resolution searches the union of the
    /// configured type's equivalent forms (<see cref="ApproverClaimTypes.EquivalentFormsOf"/>):
    /// production tokens carry the JWT inbound-MAPPED form (e.g. <c>oid</c> arrives as the
    /// objectidentifier URI), dev/test principals the short form — searching only one of them
    /// would 403 every legitimate reviewer on the other auth path. Across that union, more than
    /// one distinct value (per <c>ApproverNames.Comparer</c>) is an ambiguous identity and is
    /// rejected rather than silently first-picked — an attacker who can smuggle a second
    /// instance of the claim must not get to choose which one wins; the same value appearing
    /// under both forms counts as one. Identical logic to <c>EscalationsController.ResolveApproverName</c> —
    /// both surfaces share <c>EscalationConfig.ApproverClaimType</c> so one config knob governs
    /// reviewer identity everywhere.
    /// </summary>
    private string? ResolveReviewerId()
    {
        var claimType = _config.CurrentValue.ApproverClaimType;
        var values = ApproverClaimTypes.EquivalentFormsOf(claimType)
            .SelectMany(form => User.FindAll(form))
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(ApproverNames.Comparer)
            .ToList();

        if (values.Count == 1)
            return values[0];

        // Diagnostic detail (which claim type, how many values) goes to the log only; the HTTP
        // body stays generic so the response never teaches a caller which claim to forge.
        _logger.LogWarning(
            "Reviewer identity resolution failed: configured claim '{ApproverClaimType}' (incl. mapped forms) yielded {Count} distinct value(s) on the principal",
            claimType, values.Count);
        return null;
    }

    /// <summary>
    /// Fail-closed response for an authenticated, role-holding principal without exactly one
    /// usable reviewer identity claim — without it the audit record could not name who decided.
    /// The detail is deliberately generic; the configured claim type appears only in server logs.
    /// </summary>
    private IActionResult MissingReviewerClaim() => Problem(
        title: "Forbidden",
        detail: "The authenticated principal does not carry a usable reviewer identity; the decision cannot be attributed.",
        statusCode: StatusCodes.Status403Forbidden);

    private IActionResult MapFailure(Result result) =>
        this.FailureResponse(result, "Change-proposal operation failed");
}

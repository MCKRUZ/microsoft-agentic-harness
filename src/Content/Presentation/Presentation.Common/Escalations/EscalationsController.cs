using Application.Core.CQRS.Escalation;
using Application.Core.Validation;
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

namespace Presentation.Common.Escalations;

/// <summary>
/// REST API for answering escalations — the human side of the approval loop. Approvers list and
/// inspect the pending escalations on their roster and submit approve/deny decisions;
/// administrators cancel stuck escalations. A decision that resolves an escalation immediately
/// releases the agent turn blocked on it in this process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in mount.</b> The controller ships in <c>Presentation.Common</c> but its routes only
/// exist in hosts that called <see cref="EscalationApiMvcBuilderExtensions.AddEscalationApi"/> —
/// see <see cref="RequiresEscalationApiOptInAttribute"/>. Escalation state is an in-process
/// singleton, so the API must be co-resident with the agent workload.
/// </para>
/// <para>
/// <b>Identity from token, never from body.</b> The caller's approver name is stamped from the
/// authenticated principal's claims (claim type configured by
/// <c>EscalationConfig.ApproverClaimType</c>, allowlisted to issuer-asserted identity claims,
/// default <c>preferred_username</c>) and compared to rosters with
/// <c>ApproverNames.Comparer</c>. No request DTO carries an approver-name field, so a caller
/// cannot decide as someone else. A principal lacking the configured claim — or carrying it more
/// than once (an ambiguous identity is no identity) — is rejected with 403 (fail-closed), never
/// mapped through a fallback claim.
/// </para>
/// <para>
/// <b>Existence disclosure.</b> <c>GET /{id}</c> treats escalations as roster-private in both
/// lifecycle states: a caller outside the roster receives the same 404 as an unknown id, pending
/// or resolved (resolved outcomes carry the originating roster forward for exactly this check).
/// <c>POST /{id}/decision</c> deliberately differs — the service returns its documented 403
/// (<c>ApproverNotAuthorized</c>) for a non-roster decision on an existing escalation, which
/// reveals existence to authenticated, role-holding callers. That is the accepted design: for
/// the decision path the roster check <em>is</em> the authorization, and an honest 403 beats
/// disguising an authorization failure as absence.
/// </para>
/// </remarks>
[ApiController]
[Route("api/escalations")]
[RequiresEscalationApiOptIn]
public sealed class EscalationsController : ControllerBase
{
    /// <summary>
    /// App role required to list, read, and decide escalations. Follows the
    /// <c>AgentHub.Traces.ReadAll</c> precedent of role-gating privileged surfaces; the
    /// per-escalation approver roster then narrows which items a role holder can actually see
    /// and decide.
    /// </summary>
    public const string DecideRole = "Harness.Approvals.Decide";

    /// <summary>
    /// App role required to administratively cancel a pending escalation. Held separately from
    /// <see cref="DecideRole"/>: cancellation force-denies an escalation regardless of roster
    /// membership, which is an operator power, not an approver power.
    /// </summary>
    public const string AdminRole = "Harness.Approvals.Admin";

    private readonly IMediator _mediator;
    private readonly IOptionsMonitor<EscalationConfig> _config;
    private readonly ILogger<EscalationsController> _logger;

    /// <summary>Initializes the controller with its dependencies.</summary>
    /// <param name="mediator">The MediatR mediator used to dispatch escalation operations.</param>
    /// <param name="config">Escalation configuration; supplies the approver identity claim type.</param>
    /// <param name="logger">Logger for identity-resolution diagnostics (claim details never leave the trust boundary in responses).</param>
    public EscalationsController(
        IMediator mediator,
        IOptionsMonitor<EscalationConfig> config,
        ILogger<EscalationsController> logger)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _mediator = mediator;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Lists the pending escalations whose approver roster contains the caller.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The caller's pending escalations (possibly empty).</returns>
    /// <response code="200">The caller's roster items (may contain zero results).</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks <see cref="DecideRole"/> or has no approver identity claim.</response>
    [HttpGet]
    [Authorize(Roles = DecideRole)]
    [ProducesResponseType(typeof(IReadOnlyList<EscalationSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPending(CancellationToken cancellationToken)
    {
        if (ResolveApproverName() is not { } approverName)
            return MissingApproverClaim();

        var result = await _mediator.Send(
            new GetPendingEscalationsForApproverQuery { ApproverName = approverName },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Ok(result.Value) : MapFailure(result);
    }

    /// <summary>
    /// Reads one escalation: its pending summary while undecided (roster-private), or its
    /// resolved outcome after a verdict — the poll target following a 202 decision response.
    /// </summary>
    /// <param name="id">The escalation id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The discriminated pending/resolved detail.</returns>
    /// <response code="200">The escalation detail (pending summary or resolved outcome).</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks <see cref="DecideRole"/> or has no approver identity claim.</response>
    /// <response code="404">Unknown id — or a pending escalation whose roster excludes the caller (indistinguishable by design).</response>
    [HttpGet("{id:guid}")]
    [Authorize(Roles = DecideRole)]
    [ProducesResponseType(typeof(EscalationDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        if (ResolveApproverName() is not { } approverName)
            return MissingApproverClaim();

        var result = await _mediator.Send(
            new GetEscalationQuery { EscalationId = id, ApproverName = approverName },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Ok(result.Value) : MapFailure(result);
    }

    /// <summary>
    /// Submits the caller's approve/deny decision on a pending escalation. The approver identity
    /// is taken from the token, never from the body.
    /// </summary>
    /// <param name="id">The escalation id.</param>
    /// <param name="request">The decision: approve flag and optional reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decision status, with the final outcome when this decision resolved the escalation.</returns>
    /// <response code="200">This decision resolved the escalation; the body carries the final outcome.</response>
    /// <response code="202">Decision recorded but the escalation is still unresolved (e.g. AllOf awaiting others); poll <c>GET /{id}</c> for the verdict.</response>
    /// <response code="400">Invalid request (e.g. oversized reason).</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks <see cref="DecideRole"/>, has no approver identity claim, or is not on this escalation's roster.</response>
    /// <response code="404">No pending escalation with the given id.</response>
    /// <response code="409">
    /// Either this approver already recorded the opposite verdict (votes cannot be changed; a
    /// repeat with the same verdict echoes 202), or the escalation had already reached a verdict
    /// that failed its durable/audit write and is parked awaiting reconciliation — in which case
    /// this decision was not counted and never will be. Poll <c>GET /{id}</c> for the outcome.
    /// </response>
    [HttpPost("{id:guid}/decision")]
    [Authorize(Roles = DecideRole)]
    [ProducesResponseType(typeof(EscalationDecisionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(EscalationDecisionResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SubmitDecision(
        Guid id,
        [FromBody] SubmitEscalationDecisionRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolveApproverName() is not { } approverName)
            return MissingApproverClaim();

        var result = await _mediator.Send(new SubmitEscalationDecisionCommand
        {
            EscalationId = id,
            ApproverName = approverName,
            Approve = request.Approve,
            Verdict = request.Verdict,
            Reason = request.Reason,
            Instructions = request.Instructions
        }, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? MapDecisionStatus(result.Value!) : MapFailure(result);
    }

    /// <summary>
    /// Maps a decision status to its HTTP response. Every status that reaches this method has an
    /// explicit arm: an unmapped member falls to the 500 default and turns an ordinary,
    /// well-understood lifecycle state into a server error, which is the defect the exhaustive
    /// mapping exists to prevent.
    /// </summary>
    /// <remarks>
    /// UnknownEscalation → 404, ApproverNotAuthorized → 403, DecisionRecorded → 202, Resolved → 200.
    /// The two conflict statuses never arrive here: <see cref="SubmitEscalationDecisionCommandHandler"/>
    /// converts ConflictingDecision and AwaitingReconciliation into a Conflict failure in the
    /// Application layer, which <c>MapFailure</c> renders as a 409 — so the conflict is reported the
    /// same way to every consumer, not just this transport. That handler owns the exhaustiveness
    /// guarantee for them; the 500 default here survives only as a guard for a status added to the
    /// enum without being mapped in either place.
    /// </remarks>
    /// <param name="value">The successful decision result to translate.</param>
    private IActionResult MapDecisionStatus(SubmitEscalationDecisionResult value) => value.Status switch
    {
        EscalationDecisionStatus.UnknownEscalation => Problem(
            title: "Not found",
            detail: "No pending escalation with the given id.",
            statusCode: StatusCodes.Status404NotFound),
        EscalationDecisionStatus.ApproverNotAuthorized => Problem(
            title: "Forbidden",
            detail: "The caller is not on this escalation's approver roster.",
            statusCode: StatusCodes.Status403Forbidden),
        EscalationDecisionStatus.DecisionRecorded =>
            Accepted(new EscalationDecisionResponse(value.Status, null)),
        EscalationDecisionStatus.Resolved =>
            Ok(new EscalationDecisionResponse(value.Status, value.Outcome)),

        _ => Problem(
            title: "Escalation operation failed",
            detail: "An error occurred processing the request. See server logs for details.",
            statusCode: StatusCodes.Status500InternalServerError)
    };

    /// <summary>
    /// Administratively cancels a pending escalation, resolving it as denied and releasing any
    /// agent turn blocked on it.
    /// </summary>
    /// <param name="id">The escalation id.</param>
    /// <param name="request">The cancellation reason (required, audited).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The denial outcome recorded for the cancellation.</returns>
    /// <response code="200">The escalation was cancelled; the body carries the denial outcome.</response>
    /// <response code="400">Missing or oversized reason.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks <see cref="AdminRole"/> or has no approver identity claim.</response>
    /// <response code="404">No pending escalation with the given id.</response>
    /// <response code="409">The escalation is already resolved (or resolved concurrently) and cannot be cancelled.</response>
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Roles = AdminRole)]
    [ProducesResponseType(typeof(EscalationOutcomeSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(
        Guid id,
        [FromBody] CancelEscalationRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolveApproverName() is not { } cancelledBy)
            return MissingApproverClaim();

        var result = await _mediator.Send(new CancelEscalationCommand
        {
            EscalationId = id,
            Reason = request.Reason,
            CancelledBy = cancelledBy
        }, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Ok(result.Value) : MapFailure(result);
    }

    /// <summary>
    /// Resolves the caller's approver identity from the configured claim type, or null when the
    /// principal does not carry exactly one usable value. Resolution searches the union of the
    /// configured type's equivalent forms (<see cref="ApproverClaimTypes.EquivalentFormsOf"/>):
    /// production tokens carry the JWT inbound-MAPPED form (e.g. <c>oid</c> arrives as the
    /// objectidentifier URI), dev/test principals the short form — searching only one of them
    /// would 403 every legitimate approver on the other auth path. Across that union, more than
    /// one distinct value (per <c>ApproverNames.Comparer</c>) is an ambiguous identity and is
    /// rejected rather than silently first-picked — an attacker who can smuggle a second
    /// instance of the claim must not get to choose which one wins; the same value appearing
    /// under both forms counts as one.
    /// </summary>
    private string? ResolveApproverName()
    {
        var claimType = _config.CurrentValue.ApproverClaimType;
        var approverName = User.GetApproverNameOrNull(claimType);
        if (approverName is not null)
            return approverName;

        // Diagnostic detail (which claim type) goes to the log only; the HTTP body stays generic so
        // the response never teaches a caller which claim to forge.
        _logger.LogWarning(
            "Approver identity resolution failed: configured claim '{ApproverClaimType}' (incl. mapped forms) did not yield exactly one distinct value on the principal",
            claimType);
        return null;
    }

    /// <summary>
    /// Fail-closed response for an authenticated, role-holding principal without exactly one
    /// usable approver identity claim — without it no roster comparison is possible. The detail
    /// is deliberately generic; the configured claim type appears only in server logs.
    /// </summary>
    private IActionResult MissingApproverClaim() => Problem(
        title: "Forbidden",
        detail: "The authenticated principal does not carry a usable approver identity; no roster comparison is possible.",
        statusCode: StatusCodes.Status403Forbidden);

    private IActionResult MapFailure(Result result) =>
        this.FailureResponse(result, "Escalation operation failed");
}

// The wire-contract records below are deliberately colocated with the controller (the
// MemoryController precedent): they are this endpoint set's HTTP contract, not shared
// application models, and reading them next to the actions that bind them is the point.

/// <summary>Request body for <c>POST /api/escalations/{id}/decision</c>.</summary>
/// <param name="Approve">
/// Whether the caller approves the escalated action. Kept for callers written before
/// <paramref name="Verdict"/> existed; a request carrying only this field keeps working.
/// </param>
/// <param name="Reason">Optional free-text reason recorded with the decision (max 2000 characters).</param>
/// <param name="Verdict">
/// The caller's three-way verdict (#321). Optional and additive — when present it is preferred
/// over <paramref name="Approve"/>, and the two must agree or the request is rejected.
/// </param>
/// <param name="Instructions">
/// Steering instructions for the agent's next attempt, required when <paramref name="Verdict"/>
/// is <see cref="ApproverVerdict.Revise"/> (max 1024 characters).
/// </param>
/// <remarks>
/// Deliberately carries no approver-name field: the deciding identity always comes from the
/// authenticated token's configured claim, so it cannot be spoofed through the body.
/// </remarks>
public sealed record SubmitEscalationDecisionRequest(
    bool Approve, string? Reason = null, ApproverVerdict? Verdict = null, string? Instructions = null);

/// <summary>Request body for <c>POST /api/escalations/{id}/cancel</c>.</summary>
/// <param name="Reason">Required free-text reason for the cancellation (max 2000 characters).</param>
public sealed record CancelEscalationRequest(string Reason);

/// <summary>
/// Response body for <c>POST /api/escalations/{id}/decision</c>.
/// </summary>
/// <param name="Status">
/// What happened to the decision: <c>Resolved</c> (this decision produced the final verdict) or
/// <c>DecisionRecorded</c> (recorded, escalation still pending others). The error statuses are
/// returned as ProblemDetails, never in this shape.
/// </param>
/// <param name="Outcome">The final outcome; non-null only when <paramref name="Status"/> is <c>Resolved</c>.</param>
public sealed record EscalationDecisionResponse(
    EscalationDecisionStatus Status,
    EscalationOutcomeSummary? Outcome);

using Application.Core.CQRS.DriftDetection;
using Application.Core.Validation;
using Domain.AI.DriftDetection;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.Common.Extensions;

namespace Presentation.Common.Drift;

/// <summary>
/// REST API for the EWMA drift-monitoring subsystem: read baselines, evaluation history, and
/// the audit trail; push evaluations into the pipeline; and request baseline recalculation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in mount.</b> The controller ships in <c>Presentation.Common</c> but its routes only
/// exist in hosts that called <see cref="DriftApiMvcBuilderExtensions.AddDriftApi"/> — see
/// <see cref="RequiresDriftApiOptInAttribute"/>. Non-opted hosts answer these paths with a
/// plain 404 before authentication.
/// </para>
/// <para>
/// <b>Read/write role separation.</b> The subsystem is push-based: it has no internal
/// collector, so whoever can POST evaluations shapes the EWMA state and the history future
/// baselines are computed from — a history-poisoning vector that can mask real drift or
/// fabricate it to trigger escalations. Reads are therefore gated by
/// <see cref="ReadRole"/> while the two writes require the distinct ops role
/// <see cref="OperateRole"/>; holding one never implies the other.
/// </para>
/// <para>
/// <b>Identity from token, never from body.</b> Both writes are recorded in the drift audit
/// trail with the caller identity resolved from the authenticated principal's claims (claim
/// type configured by <c>DriftDetectionConfig.CallerIdentityClaimType</c>, allowlisted to
/// issuer-asserted identity claims, default <c>oid</c>). No request DTO carries a caller-id
/// field, so a push cannot be attributed to someone else. A principal lacking the configured
/// claim — or carrying more than one distinct value (an ambiguous identity is no identity) —
/// is rejected with 403 (fail-closed), never mapped through a fallback claim.
/// </para>
/// <para>
/// <b>No scan endpoint.</b> There is deliberately no "trigger a drift scan" route: nothing in
/// the subsystem collects scores on its own, so such an endpoint could only fake activity.
/// Callers push real evaluations or read what has been pushed.
/// </para>
/// <para>
/// <b>What a 409 body discloses.</b> Conflict responses surface the drift subsystem's own
/// message verbatim, which can echo the caller's <c>ScopeIdentifier</c> (bounded to 200 chars
/// by validation) and the configured <c>MinSamplesForBaseline</c> in
/// "Insufficient samples: 3/20". Both are accepted disclosures: the identifier is the caller's
/// own input reflected back, and the sample threshold is operational configuration an
/// <see cref="OperateRole"/> holder is already trusted with — knowing it reveals nothing about
/// other tenants, other scopes, or the data itself, and the alternative (an opaque 409) would
/// leave operators unable to tell "wrong scope" from "not enough history yet". Every other
/// failure type still maps through <see cref="ControllerBaseExtensions"/> to a generic body.
/// </para>
/// </remarks>
[ApiController]
[Route("api/drift")]
[RequiresDriftApiOptIn]
public sealed class DriftController : ControllerBase
{
    /// <summary>
    /// App role required to read baselines, drift history, and the drift audit trail. Follows
    /// the <c>Harness.*</c> read-role precedent (<c>Harness.Learnings.Read</c>).
    /// </summary>
    public const string ReadRole = "Harness.Drift.Read";

    /// <summary>
    /// App role required to push evaluations and recalculate baselines. Held separately from
    /// <see cref="ReadRole"/>: writes shift the subsystem's notion of "normal" (an operator
    /// power with poisoning potential), while reads are pure observation — neither role
    /// implies the other.
    /// </summary>
    public const string OperateRole = "Harness.Drift.Operate";

    private readonly IMediator _mediator;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<DriftController> _logger;

    /// <summary>Initializes the controller with its dependencies.</summary>
    /// <param name="mediator">The MediatR mediator used to dispatch drift operations.</param>
    /// <param name="config">Application configuration; supplies the caller identity claim type.</param>
    /// <param name="logger">Logger for identity-resolution diagnostics (claim details never leave the trust boundary in responses).</param>
    public DriftController(
        IMediator mediator,
        IOptionsMonitor<AppConfig> config,
        ILogger<DriftController> logger)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _mediator = mediator;
        _config = config;
        _logger = logger;
    }

    /// <summary>Lists the active drift baselines, optionally filtered to one scope level.</summary>
    /// <param name="scope">Optional scope filter (<c>Agent</c>, <c>Skill</c>, or <c>TaskType</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active baselines (possibly empty — nothing has pushed evaluations yet in a fresh deployment).</returns>
    /// <response code="200">The baselines (may contain zero results).</response>
    /// <response code="400">Invalid scope filter.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks <see cref="ReadRole"/>.</response>
    [HttpGet("baselines")]
    [Authorize(Roles = ReadRole)]
    [ProducesResponseType(typeof(IReadOnlyList<DriftBaseline>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetBaselines(
        [FromQuery] DriftScope? scope, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new GetDriftBaselinesQuery { Scope = scope },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Ok(result.Value) : MapFailure(result);
    }

    /// <summary>Retrieves the persisted drift scores for one scope within a bounded time window.</summary>
    /// <param name="scope">The hierarchy level to query.</param>
    /// <param name="scopeIdentifier">The entity within the scope (agent ID, skill name, or task type).</param>
    /// <param name="start">Start of the query window (inclusive).</param>
    /// <param name="end">End of the query window (inclusive); the window is capped server-side.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every evaluation recorded for the scope in the window, healthy and drifted alike.</returns>
    /// <response code="200">The drift scores (may contain zero results).</response>
    /// <response code="400">Missing/oversized identifier, unordered window, or a window over the cap.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks <see cref="ReadRole"/>.</response>
    [HttpGet("history")]
    [Authorize(Roles = ReadRole)]
    [ProducesResponseType(typeof(IReadOnlyList<DriftScore>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetHistory(
        [FromQuery] DriftScope scope,
        [FromQuery] string? scopeIdentifier,
        [FromQuery] DateTimeOffset start,
        [FromQuery] DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetDriftHistoryQuery
        {
            Scope = scope,
            ScopeIdentifier = scopeIdentifier ?? string.Empty,
            Start = start,
            End = end
        }, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Ok(result.Value) : MapFailure(result);
    }

    /// <summary>
    /// Retrieves drift audit records: detections, resolutions, baseline updates, escalation
    /// triggers, and the operator actions performed through this API with their caller
    /// identities.
    /// </summary>
    /// <param name="start">Optional window start (inclusive).</param>
    /// <param name="end">Optional window end (inclusive).</param>
    /// <param name="recordType">Optional record-type filter.</param>
    /// <param name="eventId">Optional originating-event filter.</param>
    /// <param name="maxResults">Result cap (1–1000, default 500); the most recent matches are returned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching audit records in chronological order.</returns>
    /// <response code="200">The audit records (may contain zero results).</response>
    /// <response code="400">Unordered window, unknown record type, or an out-of-range cap.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks <see cref="ReadRole"/>.</response>
    [HttpGet("audits")]
    [Authorize(Roles = ReadRole)]
    [ProducesResponseType(typeof(IReadOnlyList<DriftAuditRecord>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAudits(
        [FromQuery] DateTimeOffset? start,
        [FromQuery] DateTimeOffset? end,
        [FromQuery] DriftAuditRecordType? recordType,
        [FromQuery] Guid? eventId,
        [FromQuery] int maxResults = DriftValidationRules.DefaultAuditResults,
        CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(new GetDriftAuditsQuery
        {
            Start = start,
            End = end,
            RecordType = recordType,
            EventId = eventId,
            MaxResults = maxResults
        }, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Ok(result.Value) : MapFailure(result);
    }

    /// <summary>
    /// Pushes one set of dimension scores into the drift pipeline. The caller identity from
    /// the token is recorded in the audit trail alongside the push.
    /// </summary>
    /// <param name="request">The scope and dimension scores to evaluate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resulting drift score with per-dimension deviations and classified severity.</returns>
    /// <response code="200">The evaluation ran; the body carries the resulting score.</response>
    /// <response code="400">Invalid request (empty dimensions, out-of-range or non-finite scores, oversized identifier).</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks <see cref="OperateRole"/> or has no usable identity claim.</response>
    /// <response code="409">No baseline exists for the scope yet — establish one before evaluating against it.</response>
    [HttpPost("evaluations")]
    [Authorize(Roles = OperateRole)]
    [ProducesResponseType(typeof(DriftScore), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PushEvaluation(
        [FromBody] PushDriftEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolveCallerId() is not { } callerId)
            return MissingCallerIdentity();

        var result = await _mediator.Send(new PushDriftEvaluationCommand
        {
            Scope = request.Scope,
            ScopeIdentifier = request.ScopeIdentifier,
            Dimensions = request.Dimensions,
            CallerId = callerId
        }, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Ok(result.Value) : MapFailure(result);
    }

    /// <summary>
    /// Recalculates the identified baseline from its scope's recent evaluation history,
    /// replacing the current snapshot. The caller identity from the token is recorded in the
    /// audit trail alongside the request.
    /// </summary>
    /// <param name="id">The baseline id to recalculate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new baseline snapshot.</returns>
    /// <response code="200">The baseline was recalculated; the body carries the new snapshot.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks <see cref="OperateRole"/> or has no usable identity claim.</response>
    /// <response code="404">No baseline with the given id.</response>
    /// <response code="409">Drift detection is disabled, or the window holds too few samples to recalculate from.</response>
    [HttpPost("baselines/{id:guid}/recalculate")]
    [Authorize(Roles = OperateRole)]
    [ProducesResponseType(typeof(DriftBaseline), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RecalculateBaseline(Guid id, CancellationToken cancellationToken)
    {
        if (ResolveCallerId() is not { } callerId)
            return MissingCallerIdentity();

        var result = await _mediator.Send(new RecalculateDriftBaselineCommand
        {
            BaselineId = id,
            CallerId = callerId
        }, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Ok(result.Value) : MapFailure(result);
    }

    /// <summary>
    /// Resolves the caller identity for the audit trail via <see cref="DriftCallerIdentity"/>,
    /// the shared resolver the host's rate-limit partitioner also uses. Absent or ambiguous
    /// identities yield null and are rejected fail-closed by the caller.
    /// </summary>
    private string? ResolveCallerId()
    {
        var claimType = _config.CurrentValue.AI.DriftDetection.CallerIdentityClaimType;
        var callerId = DriftCallerIdentity.Resolve(User, claimType);

        if (callerId is null)
        {
            // Diagnostic detail (which claim type) goes to the log only; the HTTP body stays
            // generic so the response never teaches a caller which claim to forge.
            _logger.LogWarning(
                "Drift caller identity resolution failed: configured claim '{CallerIdentityClaimType}' (incl. mapped forms) did not yield exactly one distinct value on the principal",
                claimType);
        }

        return callerId;
    }

    /// <summary>
    /// Fail-closed response for an authenticated, role-holding principal without exactly one
    /// usable identity claim — without it the write cannot be attributed in the audit trail.
    /// The detail is deliberately generic; the configured claim type appears only in server logs.
    /// </summary>
    private IActionResult MissingCallerIdentity() => Problem(
        title: "Forbidden",
        detail: "The authenticated principal does not carry a usable caller identity; the operation cannot be attributed in the audit trail.",
        statusCode: StatusCodes.Status403Forbidden);

    private IActionResult MapFailure(Result result) =>
        this.FailureResponse(result, "Drift operation failed");
}

// The wire-contract record below is deliberately colocated with the controller (the
// EscalationsController precedent): it is this endpoint set's HTTP contract, not a shared
// application model, and reading it next to the action that binds it is the point.

/// <summary>Request body for <c>POST /api/drift/evaluations</c>.</summary>
/// <param name="Scope">The hierarchy level of the evaluation.</param>
/// <param name="ScopeIdentifier">The entity within the scope (agent ID, skill name, or task type).</param>
/// <param name="Dimensions">Dimension scores to evaluate — finite values in [0, 1].</param>
/// <remarks>
/// Deliberately carries no caller-id field: the pushing identity always comes from the
/// authenticated token's configured claim, so a push cannot be attributed to someone else
/// through the body.
/// </remarks>
public sealed record PushDriftEvaluationRequest(
    DriftScope Scope,
    string ScopeIdentifier,
    IReadOnlyDictionary<DriftDimension, double> Dimensions);

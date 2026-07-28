using Application.Core.CQRS.Autonomy;
using Domain.AI.Changes;
using Domain.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Presentation.Common.Extensions;

namespace Presentation.Common.Governance;

/// <summary>
/// Read-only REST API over the autonomy governance configuration: the effective autonomy tier
/// per subagent type, and a side-effect-free preview of the graded-autonomy decision for a
/// hypothetical action. Autonomy tiers are pure configuration enforced by the MediatR pipeline;
/// there is no mutable runtime state, so this surface deliberately exposes no writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in mount.</b> The controller ships in <c>Presentation.Common</c> but its routes only
/// exist in hosts that called <see cref="AutonomyApiMvcBuilderExtensions.AddAutonomyApi"/> —
/// see <see cref="RequiresAutonomyApiOptInAttribute"/>. The answers are computed from the
/// host's own configuration and profile registry, so the API belongs in the workload host
/// whose governance posture it describes.
/// </para>
/// <para>
/// <b>Parity with enforcement.</b> Both endpoints dispatch to handlers that call the same
/// shared services the enforcement path uses (<c>IAutonomyTierResolver</c>,
/// <c>IAutonomyDecisionEvaluator</c>) — the preview cannot drift from what enforcement would
/// actually decide for the same inputs.
/// </para>
/// </remarks>
[ApiController]
[Route("api/governance/autonomy")]
[RequiresAutonomyApiOptIn]
public sealed class AutonomyController : ControllerBase
{
    /// <summary>
    /// App role required to read autonomy governance state. Follows the
    /// <c>Harness.Learnings.Read</c> precedent of role-gating read-only governance surfaces:
    /// tier assignments and decision policy describe the deployment's security posture and are
    /// operator information, not anonymous data.
    /// </summary>
    public const string ReadRole = "Harness.Governance.Read";

    private readonly IMediator _mediator;

    /// <summary>Initializes the controller with its dependencies.</summary>
    /// <param name="mediator">The MediatR mediator used to dispatch autonomy read operations.</param>
    public AutonomyController(IMediator mediator)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        _mediator = mediator;
    }

    /// <summary>
    /// Reads the effective autonomy tier for a subagent type — profile registry first, then
    /// the configured default. Pure config read; nothing is written or audited.
    /// </summary>
    /// <param name="subagentType">The subagent type name (case-insensitive, e.g. <c>Explore</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The effective tier for the subagent type.</returns>
    /// <response code="200">The effective autonomy tier.</response>
    /// <response code="400">The subagent type value is empty or exceeds the length cap.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks <see cref="ReadRole"/>.</response>
    /// <response code="404">No subagent type with the given name exists.</response>
    [HttpGet("tiers/{subagentType}")]
    [Authorize(Roles = ReadRole)]
    [ProducesResponseType(typeof(AutonomyTierDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTier(string subagentType, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new GetAutonomyTierQuery { SubagentType = subagentType },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Ok(result.Value) : MapFailure(result);
    }

    /// <summary>
    /// Previews the graded-autonomy decision for a hypothetical action: would it auto-approve,
    /// require human approval, or be forbidden? Side-effect-free — nothing is executed,
    /// escalated, audited, or persisted.
    /// </summary>
    /// <param name="request">The hypothetical action to evaluate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decision the evaluator would reach, with the rule that drove it.</returns>
    /// <response code="200">The preview decision.</response>
    /// <response code="400">A required field is missing, or a blast radius / target kind value does not name a defined member.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks <see cref="ReadRole"/>.</response>
    /// <response code="404">No subagent type with the given name exists.</response>
    [HttpPost("decision-preview")]
    [Authorize(Roles = ReadRole)]
    [ProducesResponseType(typeof(AutonomyDecisionPreviewResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PreviewDecision(
        [FromBody] AutonomyDecisionPreviewRequest request, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new PreviewAutonomyDecisionQuery
            {
                SubagentType = request.SubagentType ?? string.Empty,
                BlastRadius = request.BlastRadius ?? string.Empty,
                TargetKind = request.TargetKind ?? PreviewAutonomyDecisionQuery.DefaultTargetKind,
                IsStateChange = request.IsStateChange,
                SkillKey = request.SkillKey,
            },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Ok(result.Value) : MapFailure(result);
    }

    private IActionResult MapFailure(Result result) =>
        this.FailureResponse(result, "Autonomy governance read failed");
}

// The wire-contract record below is deliberately colocated with the controller (the
// EscalationsController precedent): it is this endpoint set's HTTP contract, not a shared
// application model, and reading it next to the action that binds it is the point.

/// <summary>Request body for <c>POST /api/governance/autonomy/decision-preview</c>.</summary>
/// <param name="SubagentType">The subagent type name the preview is for (case-insensitive, e.g. <c>Execute</c>).</param>
/// <param name="BlastRadius">The proposed action's blast radius name (case-insensitive, e.g. <c>Medium</c>).</param>
/// <param name="TargetKind">The proposed action's target kind name. Optional at the wire: when
/// omitted or null, the preview evaluates the action as <c>Unspecified</c> (the same way
/// non-target-specific proposals are evaluated); when supplied, it must name a defined member.</param>
/// <param name="IsStateChange">Whether the proposed action mutates state. Defaults to false.</param>
/// <param name="SkillKey">The skill key the action is attributed to, or null when not skill-attributable.</param>
public sealed record AutonomyDecisionPreviewRequest(
    string? SubagentType,
    string? BlastRadius,
    string? TargetKind = null,
    bool IsStateChange = false,
    string? SkillKey = null);

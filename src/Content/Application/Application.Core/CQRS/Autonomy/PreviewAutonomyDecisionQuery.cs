using Domain.AI.Changes;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Autonomy;

/// <summary>
/// Previews the graded-autonomy decision for a hypothetical action without executing anything:
/// "would this action, by this subagent, be allowed, routed through approval, or forbidden?".
/// Strictly side-effect-free — the same pure evaluation the enforcement path runs, minus the
/// enforcement. No audit records, no state changes, no escalations raised.
/// </summary>
/// <remarks>
/// Enum inputs travel as strings because this is the HTTP boundary shape. The handler parses
/// them by name: an unknown subagent type is <c>NotFound</c> (the resource being asked about
/// does not exist); an unknown blast radius or target kind is a validation failure (the request
/// itself is malformed).
/// </remarks>
public sealed record PreviewAutonomyDecisionQuery : IRequest<Result<AutonomyDecisionPreviewResult>>
{
    /// <summary>
    /// The default <see cref="TargetKind"/> value when the caller does not supply one. Single
    /// source of truth for the "omitted target kind evaluates as <c>Unspecified</c>" rule —
    /// the HTTP controller references this constant rather than re-deriving it.
    /// </summary>
    public const string DefaultTargetKind = nameof(ChangeTargetKind.Unspecified);

    /// <summary>The subagent type name the preview is for (case-insensitive, e.g. <c>Execute</c>).</summary>
    public required string SubagentType { get; init; }

    /// <summary>The proposed action's blast radius name (case-insensitive, e.g. <c>Medium</c>).</summary>
    public required string BlastRadius { get; init; }

    /// <summary>
    /// The proposed action's target kind name (case-insensitive). Defaults to
    /// <c>Unspecified</c>, matching how non-target-specific proposals are evaluated.
    /// </summary>
    public string TargetKind { get; init; } = DefaultTargetKind;

    /// <summary>Whether the proposed action mutates state (writes a file, applies a deployment, runs a migration).</summary>
    public bool IsStateChange { get; init; }

    /// <summary>
    /// The skill key the action is attributed to, or null when not skill-attributable.
    /// Drives the evaluator's per-skill narrowing and state-changer opt-in checks.
    /// </summary>
    public string? SkillKey { get; init; }
}

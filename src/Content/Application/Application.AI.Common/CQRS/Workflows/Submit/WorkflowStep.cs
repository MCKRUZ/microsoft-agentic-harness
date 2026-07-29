using Domain.AI.Governance;
using Domain.AI.Planner;

namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// A single step within a submitted <see cref="WorkflowDefinition"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Name"/> is the caller's local handle for this step and the only way
/// <see cref="WorkflowEdge"/> refers to it. It is not persisted as an identifier — the mapper mints a
/// <c>PlanStepId</c> — so two callers may use the same step names without colliding.
/// </para>
/// <para>
/// <see cref="RequiredAutonomyLevel"/> is a *ceiling request*, not a grant. A step may declare that it
/// needs a given autonomy level, and the caller's capability envelope decides whether that level is
/// available; a step asking for more than the envelope permits fails closed when it runs. Declaring a
/// high level here does not obtain it.
/// </para>
/// </remarks>
public sealed record WorkflowStep
{
    /// <summary>
    /// The caller's name for this step, unique within the submission. Used by <see cref="WorkflowEdge"/>
    /// to reference it, and carried through as the persisted step's display name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Which kind of step this is. Must agree with the discriminator on <see cref="Configuration"/> —
    /// a mismatch is a validation failure rather than a silent preference for one or the other.
    /// </summary>
    public required StepType Type { get; init; }

    /// <summary>Step-specific configuration, discriminated by the same <c>type</c> value.</summary>
    public required WorkflowStepConfiguration Configuration { get; init; }

    /// <summary>
    /// Optional retry policy. When omitted, the host's default applies. Note that a governance denial
    /// is terminal and is never retried regardless of what is requested here — retrying a denied step
    /// would only re-ask a question already answered.
    /// </summary>
    public WorkflowRetrySettings? Retry { get; init; }

    /// <summary>
    /// Optional per-step timeout. When omitted, the host's default applies. Values above the host's
    /// configured ceiling are rejected rather than clamped.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Optional autonomy level this step requires. Subject to the caller's envelope ceiling — see the
    /// type remarks. When omitted, the plan-level default applies.
    /// </summary>
    public AutonomyLevel? RequiredAutonomyLevel { get; init; }
}

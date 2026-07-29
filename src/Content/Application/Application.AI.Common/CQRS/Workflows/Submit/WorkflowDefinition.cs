namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// A workflow as submitted by an external caller over HTTP: a named set of steps and the directed
/// edges connecting them. Mapped to a <c>Domain.AI.Planner.PlanGraph</c> and persisted as an
/// owner-scoped plan.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every identifier in the persisted plan is minted server-side.</strong> A caller names its
/// steps with arbitrary strings and refers to them by those names in <see cref="Edges"/>; the mapper
/// assigns the real <c>PlanStepId</c> and <c>PlanId</c> values. The domain model permits
/// caller-supplied ids — <c>SavePlanAsync</c> probes for a collision — but accepting them on a public
/// surface would let one caller's submission collide with, or deliberately target, another's
/// identifier space. Names are local to the submission; ids are not.
/// </para>
/// <para>
/// <strong>Edges are the single source of truth for control flow.</strong> The domain's
/// <c>ConditionalBranchConfig</c> restates its branch targets inside the step configuration, which
/// means a plan can be authored where the configuration and the edge list disagree. On the wire, a
/// conditional step declares only its condition; the mapper derives the true/false targets from the
/// labelled edges, so the two cannot diverge because there is only one of them.
/// </para>
/// <para>
/// Structural validity — cycles, referential integrity, reachability, branch completeness — is not
/// re-implemented here. <c>PlanValidator</c> already enforces all of it via Kahn's algorithm, and
/// <c>SubmitWorkflowCommandHandler</c> runs it directly on the mapped graph before persisting, so a
/// structurally broken submission is refused rather than stored and left to fail on first execution.
/// What this contract adds is admission bounding, which <c>PlanValidator</c> has no opinion about; see
/// <c>WorkflowSubmissionConfig</c>.
/// </para>
/// </remarks>
public sealed record WorkflowDefinition
{
    /// <summary>
    /// Human-readable name describing what this workflow does. Carried through to the persisted plan
    /// and surfaced in status responses, so it is the label an operator sees when triaging a run.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The workflow's steps. Each step's <see cref="WorkflowStep.Name"/> must be unique within the
    /// submission, because <see cref="Edges"/> refer to steps by name.
    /// </summary>
    public required IReadOnlyList<WorkflowStep> Steps { get; init; }

    /// <summary>
    /// Directed edges connecting the steps, referring to them by <see cref="WorkflowStep.Name"/>.
    /// An edge naming a step that does not exist is a validation failure, not a silently dropped edge.
    /// </summary>
    public required IReadOnlyList<WorkflowEdge> Edges { get; init; }

    /// <summary>
    /// Optional plan-level execution settings. When omitted, the host's defaults apply. Values that
    /// exceed the host's configured ceilings are rejected rather than silently clamped — a caller that
    /// asked for a 30-minute timeout and received 60 seconds would otherwise discover the difference
    /// only from a timeout in production.
    /// </summary>
    public WorkflowExecutionSettings? Configuration { get; init; }
}

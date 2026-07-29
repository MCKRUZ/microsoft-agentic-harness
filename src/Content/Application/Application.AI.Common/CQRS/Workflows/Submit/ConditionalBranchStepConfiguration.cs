using Domain.AI.Planner;
namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// Configuration for a step that evaluates a condition and follows one of two outgoing branches.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only the condition lives here — the branch targets are the edges.</strong> The domain type
/// also carries explicit true/false target ids, and omitting them from the wire is deliberate: with
/// both present, a submission can declare targets in the configuration that disagree with the edges,
/// and something then has to decide which of the two contradictory answers is authoritative. Here
/// there is one answer. The step declares what it is asking; the two labelled outgoing edges declare
/// where each outcome leads.
/// </para>
/// <para>
/// A step of this type requires exactly one outgoing edge labelled as the true arm and one as the
/// false arm. Branch completeness is enforced by the existing plan validator once the plan is built,
/// so a workflow that branches into nowhere is rejected before it is stored.
/// </para>
/// </remarks>
public sealed record ConditionalBranchStepConfiguration : WorkflowStepConfiguration
{
    /// <inheritdoc />
    public override StepType StepType => StepType.ConditionalBranch;

    /// <summary>
    /// The expression evaluated to choose a branch. Evaluated against the workflow's own execution
    /// state by the host's expression evaluator — it is not a general-purpose scripting hook, and an
    /// expression referencing anything outside that state is a validation failure rather than a
    /// runtime surprise.
    /// </summary>
    public required string ConditionExpression { get; init; }
}

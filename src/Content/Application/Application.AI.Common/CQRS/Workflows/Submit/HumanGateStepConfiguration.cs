using Domain.AI.Escalation;
using Domain.AI.Planner;

namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// Configuration for a step that pauses the workflow until a human approves or denies it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Admitted only when the host can say who may answer it.</strong> Every name in
/// <see cref="Approvers"/> must appear in <c>AI.WorkflowSubmission.PermittedApprovers</c>, which ships
/// empty — so a host that has not named its approvers refuses every gate. A gate naming someone the
/// host does not recognise cannot be answered at all: the workflow would run, reach it, park, and be
/// failed by the parked-run ceiling however long later. Refusing at submission makes that a 400 the
/// author can act on rather than a workflow that only ever hangs.
/// </para>
/// <para>
/// <strong>The submitter may not be an approver of its own gate.</strong> A gate its author can answer
/// is not an approval — the workflow pauses and continues on the say-so of the person who wrote it,
/// while the audit record shows that a human decided. The submitter's identity is taken from its
/// token, through the same configured claim the decision path reads, so the comparison is between two
/// forms of the same name.
/// </para>
/// <para>
/// Names are matched case-insensitively (<c>ApproverNames.Comparer</c>), and a decision is attributed
/// to the identity on the approver's own token — never to a name supplied in the decision request.
/// </para>
/// </remarks>
public sealed record HumanGateStepConfiguration : WorkflowStepConfiguration
{
    /// <inheritdoc />
    public override StepType StepType => StepType.HumanGate;

    /// <summary>
    /// The message shown to approvers explaining what they are being asked to approve. This is the
    /// only context most approvers will have, so an empty or generic message is a validation failure.
    /// </summary>
    public required string EscalationMessage { get; init; }

    /// <summary>
    /// How many of the named approvers must agree — all of them, any one, or a quorum.
    /// </summary>
    public required ApprovalStrategy ApprovalStrategy { get; init; }

    /// <summary>
    /// Identities permitted to decide this gate. Must be non-empty: a gate with no approvers can never
    /// be answered, which is the same trap as the unbuilt answering surface.
    /// </summary>
    public IReadOnlyList<string> Approvers { get; init; } = [];

    /// <summary>
    /// Risk level shown to approvers and used by graded-autonomy policy. Higher risk can tighten a
    /// decision to require explicit approval; it never loosens one.
    /// </summary>
    public RiskLevel? RiskLevel { get; init; }

    /// <summary>
    /// How long to wait for a decision before the gate times out. Bounded by the host's ceiling — an
    /// unbounded gate is a workflow that holds its execution slot indefinitely.
    /// </summary>
    public TimeSpan? Timeout { get; init; }
}

using Domain.AI.Escalation;
using Domain.AI.Planner;

namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// Configuration for a step that pauses the workflow until a human approves or denies it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Rejected at submission until the answering surface exists.</strong> A workflow containing a
/// human gate would start, reach the gate, park, and wait indefinitely — because the endpoint that
/// answers a gate and resumes the parked workflow is not yet built. Accepting such a submission would
/// hand a caller a workflow that can only ever hang, so the validator refuses it with an explicit
/// reason rather than storing a trap. The restriction lifts when the answering surface ships; the
/// contract is defined now so it does not change shape at that point.
/// </para>
/// <para>
/// <see cref="Approvers"/> names who may decide. Names are matched case-insensitively against the
/// roster, and a decision is attributed to the identity on the approver's own token — never to a name
/// supplied in the decision request. A caller cannot nominate itself as approver of its own gate and
/// then self-approve by asserting an identity in a request body.
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

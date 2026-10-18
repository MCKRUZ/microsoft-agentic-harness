namespace Domain.AI.Escalation;

/// <summary>
/// Why a plan-step escalation was raised, which decides what an approval of it is allowed to do.
/// </summary>
/// <remarks>
/// <para>
/// Two producers park a step in <c>Blocked</c> behind an escalation, and an approval means different
/// things for each: a human gate asked its own approvers whether the plan may continue, while a step
/// that failed after exhausting its retries asked a supervisor whether to try again. Without the kind
/// a supervisor approving "step failed" on a gate would complete a gate whose configured approvers and
/// strategy never saw the request.
/// </para>
/// <para>
/// <b>There is deliberately no zero member.</b> A binding stored before this field existed reads as
/// <c>0</c>, which names neither kind, so it matches nothing and the step it blocks is failed rather
/// than released. That is the fail-closed reading of an old record.
/// </para>
/// </remarks>
public enum EscalationBindingKind
{
    /// <summary>
    /// A human gate's own approval request, answered by the approvers the gate names. Approval completes
    /// the gate.
    /// </summary>
    GateApproval = 1,

    /// <summary>
    /// A step failed after exhausting its retry policy and escalated to a supervisor. Approval re-runs the
    /// step; it never completes it, whatever the step's type.
    /// </summary>
    FailureRetry = 2
}

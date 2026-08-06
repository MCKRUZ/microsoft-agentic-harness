namespace Domain.Common.Config.AI.Governance;

/// <summary>
/// Routes an approval-required verdict on the agent's live tool-call path to the human escalation
/// workflow, instead of refusing the call outright. Bound from
/// <c>AppConfig:AI:Governance:ToolApproval</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this changes.</strong> When the tool governor resolves a call to "requires approval"
/// it has always recorded <c>PendingApproval</c> and blocked — nobody was ever asked. With this
/// enabled the governor instead raises an escalation naming the tool and its arguments, waits for a
/// human decision, and lets the call proceed only if it is approved. A denial, a timeout, or any
/// failure to reach a decision still blocks, so the change can only ever turn a block into an
/// approved call — never the reverse.
/// </para>
/// <para>
/// <strong>Off by default, and doubly gated.</strong> Routing requires both this
/// <see cref="Enabled"/> flag and <see cref="EscalationConfig.Enabled"/>. With either off the
/// governor's behaviour is exactly as before (block, fail-closed), so existing deployments are
/// unchanged until an operator opts in and names a roster.
/// </para>
/// <para>
/// <strong>This blocks the agent's turn.</strong> The tool call is suspended while a human decides,
/// which is the point — an advisory observer cannot stop an action that has already happened. The
/// cost is real latency in the turn, bounded by <see cref="TimeoutSeconds"/>. Hosts on a latency
/// budget (a real-time voice agent, for instance) should keep the timeout short and accept the
/// timeout action as the answer rather than disabling the gate.
/// </para>
/// </remarks>
public sealed class ToolApprovalConfig
{
    /// <summary>
    /// Whether an approval-required tool call is routed to a human instead of being refused.
    /// Off by default. Has no effect unless <see cref="EscalationConfig.Enabled"/> is also true.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The approver roster consulted for tool-call approvals. Names are matched by the escalation
    /// service using its own case-insensitive comparer, and should be authored in the same form as
    /// <see cref="EscalationConfig.ApproverClaimType"/> resolves (object ids in production).
    /// </summary>
    /// <remarks>
    /// An empty roster with <see cref="Enabled"/> true is a misconfiguration, not an open door: no
    /// escalation is raised and the call is refused, because an escalation nobody can answer would
    /// stall the turn until it timed out. The condition is logged as a warning on first use.
    /// </remarks>
    public List<string> Approvers { get; init; } = [];

    /// <summary>
    /// How long to wait for a decision before the escalation's timeout action decides the call.
    /// Null inherits <see cref="EscalationConfig.DefaultTimeoutSeconds"/>. This is the upper bound
    /// on how long a single tool call can stall the agent's turn.
    /// </summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// The blast radius at or above which a tool call is raised at <c>Critical</c> escalation
    /// priority rather than <c>Blocking</c> — notifying every approver at once instead of following
    /// the approval strategy. Defaults to <c>Critical</c>.
    /// </summary>
    /// <remarks>
    /// Expressed as the string name of a <c>BlastRadius</c> member ("Trivial", "Low", "Medium",
    /// "High", "Critical"). An unparseable value is treated as <c>Critical</c> and logged, so a typo
    /// narrows who is paged rather than changing whether the gate fires.
    /// </remarks>
    public string CriticalAtBlastRadius { get; init; } = "Critical";
}

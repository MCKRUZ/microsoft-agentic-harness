namespace Domain.AI.Governance;

/// <summary>
/// An immutable snapshot of the governance decisions an agent's tool calls passed through
/// during a turn or conversation. Produced by <c>IToolInvocationGovernor</c> and surfaced on
/// the agent-turn and conversation results so an evaluation can grade the agent's governance
/// behaviour <em>independently of whether the task succeeded</em> — the core anti-"governance
/// theater" instrument from Loop Engineering.
/// </summary>
/// <remarks>
/// This is a read-only projection. The governor accumulates decisions in a mutable, thread-safe
/// collector as the agent runs, then emits this snapshot at the boundary.
/// </remarks>
public sealed record GovernanceTrace
{
    /// <summary>An empty trace: enforcement off and no decisions recorded.</summary>
    public static GovernanceTrace Empty { get; } = new();

    /// <summary>
    /// Whether per-invocation governance enforcement was active. When false the governor ran in
    /// observe-only mode (or was not engaged) and denials were not blocking — an eval should treat
    /// any "would-deny" decisions as ungoverned rather than enforced.
    /// </summary>
    public bool EnforcementEnabled { get; init; }

    /// <summary>The individual per-tool decisions, in the order the agent attempted the calls.</summary>
    public IReadOnlyList<ToolDecisionRecord> ToolDecisions { get; init; } = [];

    /// <summary>Total number of tool invocations the governor evaluated.</summary>
    public int ToolInvocationCount => ToolDecisions.Count;

    /// <summary>Number of invocations that were allowed.</summary>
    public int AllowedCount => ToolDecisions.Count(d => d.Outcome == ToolDecisionOutcome.Allowed);

    /// <summary>Number of invocations that were denied (blocked when enforced).</summary>
    public int DeniedCount => ToolDecisions.Count(d => d.Outcome == ToolDecisionOutcome.Denied);

    /// <summary>True when at least one tool call hit a human approval gate.</summary>
    public bool ApprovalGateEncountered => ToolDecisions.Any(d => d.RequiredApproval);

    /// <summary>True when at least one approval gate was satisfied by a human.</summary>
    public bool ApprovalGranted => ToolDecisions.Any(d => d.ApprovalGranted);

    /// <summary>
    /// True when a tool that required approval nonetheless executed without one — the governance
    /// hole an outcome-blind eval must flag as a hard failure. With enforcement on this should
    /// never be true; it surfaces config gaps and observe-only runs.
    /// </summary>
    public bool ApprovalBypassed =>
        ToolDecisions.Any(d => d.RequiredApproval && !d.ApprovalGranted && !d.Enforced
            && d.Outcome is ToolDecisionOutcome.Allowed);

    /// <summary>Distinct escalation reason codes raised during the turn/conversation.</summary>
    public IReadOnlyList<string> EscalationReasonCodes { get; init; } = [];

    /// <summary>
    /// Merges several traces (e.g. per-turn traces of a conversation) into one. Decisions are
    /// concatenated in order; flags and reason codes are unioned; enforcement is true when any
    /// component had it on.
    /// </summary>
    public static GovernanceTrace Merge(IEnumerable<GovernanceTrace> traces)
    {
        ArgumentNullException.ThrowIfNull(traces);

        var decisions = new List<ToolDecisionRecord>();
        var reasonCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enforcement = false;

        foreach (var trace in traces)
        {
            if (trace is null)
                continue;
            decisions.AddRange(trace.ToolDecisions);
            foreach (var code in trace.EscalationReasonCodes)
                reasonCodes.Add(code);
            enforcement |= trace.EnforcementEnabled;
        }

        return new GovernanceTrace
        {
            EnforcementEnabled = enforcement,
            ToolDecisions = decisions,
            EscalationReasonCodes = reasonCodes.Count > 0 ? reasonCodes.ToList() : []
        };
    }
}

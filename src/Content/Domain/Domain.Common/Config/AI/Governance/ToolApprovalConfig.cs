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
    /// Null inherits <see cref="EscalationConfig.DefaultTimeoutSeconds"/> (300s). This is the upper
    /// bound on how long a single tool call can stall the agent's turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Must be shorter than the direct-invocation deadline, or approvals cannot succeed on
    /// the Execution API.</strong> A direct tool invocation bounds its whole request —
    /// authorization included — by <c>DirectToolInvocation.InvocationTimeout</c>, which defaults to
    /// 30 seconds. Since this setting defaults to 300, a host that enables approval routing and
    /// leaves both defaults gets every approval-required direct invocation cancelled at 30s and
    /// reported as denied, while the caller's HTTP request is held for the full 30s to get there.
    /// The agent conversation path has no such ceiling and is unaffected.
    /// </para>
    /// <para>
    /// A synchronous HTTP call is a poor place to wait on a human at all, so the recommended posture
    /// for hosts that expose direct invocation is either to keep this well under
    /// <c>InvocationTimeout</c> and accept that approvers have seconds to answer, or to leave
    /// approval routing for the agent path and let the Execution API's capability envelope do the
    /// gating.
    /// </para>
    /// <para>
    /// <strong>On the plan-execution path this occupies a concurrency slot, not just a call.</strong>
    /// A plan step whose tool resolves to "requires approval" holds its executor slot for as long as
    /// the approver takes. The plan executor runs a bounded number of steps at once
    /// (<c>MaxParallelSteps</c>, 10 by default), so enough simultaneously-pending approvals stall the
    /// whole DAG rather than one step — including steps that needed no approval at all. Hosts running
    /// plans unattended should either keep this timeout short, or express the checkpoint as an
    /// explicit <c>HumanGate</c> step — the plan-native way to wait on a person, which queues its
    /// escalation and returns immediately with the step recorded <c>Blocked</c>, releasing the slot
    /// instead of holding one open for the duration of a human's attention.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Whether a <c>Revise</c> verdict's steering instructions are relayed to the model on the
    /// refused tool call, instead of the call seeing only the generic denial every other refusal
    /// gets. Off by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the one deliberate exception to "an approver's free text is never relayed
    /// to the model."</strong> Turning it on lets a rostered, authenticated, audited human put
    /// text in front of the agent — something no approver could do before. The instructions are
    /// still sanitized through the same chain that scrubs tool output, explicitly attributed as
    /// human feedback rather than a system directive, and length-capped by
    /// <see cref="MaxRelayedInstructionsLength"/>. An operator who has not opted in sees no change:
    /// a Revise verdict blocks the call exactly like a Deny, with no model-facing difference,
    /// same as before this setting existed.
    /// </para>
    /// <para>
    /// Has no effect unless a Revise verdict can actually be reached, which itself requires
    /// <see cref="EscalationConfig.Revision"/> to permit at least one round.
    /// </para>
    /// </remarks>
    public bool RelayRevisionInstructionsToModel { get; init; }

    /// <summary>
    /// The character length a relayed Revise instruction is truncated to before it reaches the
    /// model, when <see cref="RelayRevisionInstructionsToModel"/> is on. Defaults to 1000.
    /// </summary>
    /// <remarks>
    /// A soft, display-oriented cap distinct from
    /// <c>EscalationRequestInvariants.MaxRevisionInstructionsLength</c> (4096) — that ceiling
    /// exists to keep a stored <c>EscalationRequest</c> constructible at all; this one keeps what
    /// actually reaches model context short enough to be worth reading. Re-clamped defensively
    /// against the hard ceiling regardless of what an operator configures here, the same pattern
    /// <c>EscalationConfig.RetryAttribution.MaxPriorFailureLength</c> uses.
    /// </remarks>
    public int MaxRelayedInstructionsLength { get; init; } = 1000;
}

using Domain.AI.Governance;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// The turn's governance audit trail, as a thing that can be written to and snapshotted — separately
/// from the components that decide what goes into it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is its own type.</strong> Accumulating the trail used to be
/// <see cref="IToolInvocationGovernor"/>'s second job, and it was the only reason that type held
/// mutable state at all. The two jobs have different consumers: the authorization logic only ever
/// <em>writes</em> here, while tests, diagnostics, bundle-run reporting and the dashboard only ever
/// <em>read</em>. Splitting them means the trail can be asserted on without standing up a governor and
/// its twelve dependencies, and the governor is left stateless.
/// </para>
/// <para>
/// <strong>One record per decision.</strong> Every stage that can stop a tool call writes here exactly
/// once for that call — the governor for its own verdicts via <see cref="Record"/>, and every other
/// stage on its own behalf via <see cref="RecordDownstreamBlock"/>. A stage that writes a second line
/// for a call another stage already recorded makes every such call count twice for anyone tallying
/// denials from the audit stream, which is why <see cref="RecordDownstreamBlock"/> writes no audit
/// entry of its own — see its remarks.
/// </para>
/// <para>
/// Scoped to one agent turn, and reset between turns by the admission chain. Nested MediatR sends
/// within a conversation share one DI scope, so without that reset <see cref="Snapshot"/> would return
/// the cumulative list and per-turn traces would double-count when aggregated.
/// </para>
/// </remarks>
public interface IGovernanceTraceRecorder
{
    /// <summary>
    /// Whether this turn counts as governed: enforcement was active when a call was authorized
    /// (<see cref="MarkEnforced"/>), <em>or</em> it is active right now.
    /// </summary>
    /// <remarks>
    /// Both halves are load-bearing. The observed half survives a bundle run whose ambient capability
    /// envelope has already torn down by the time the trace is assembled — a turn that authorized under
    /// enforcement is still an enforced turn. The live half covers a turn under global enforcement that
    /// never reached the governor at all, either because it made no tool calls or because a stage ahead
    /// of the governor refused the only one it tried.
    /// </remarks>
    bool EnforcementEnabled { get; }

    /// <summary>
    /// Notes that a call was authorized while enforcement was active, so this turn keeps reporting as
    /// governed after any ambient enforcement signal has gone away.
    /// </summary>
    void MarkEnforced();

    /// <summary>Appends one decision to the turn's trail.</summary>
    /// <param name="decision">The decision, as the recording stage wants it to read to an auditor.</param>
    void Record(ToolDecisionRecord decision);

    /// <summary>
    /// Raises an escalation reason code for the turn. Codes are deduplicated case-insensitively, so a
    /// stage that detects the same condition repeatedly contributes one code rather than one per
    /// occurrence.
    /// </summary>
    /// <param name="reasonCode">
    /// The stable code, e.g. <c>progress.spin_detected</c>. Blank is rejected: a code nothing can be
    /// keyed off is noise on an audit trail.
    /// </param>
    void RecordEscalation(string reasonCode);

    /// <summary>Takes an immutable snapshot of everything recorded so far this turn.</summary>
    /// <returns>
    /// The trace. <see cref="GovernanceTrace.Empty"/> — by reference — when the turn was ungoverned and
    /// nothing was recorded, so a caller can cheaply tell "nothing happened" from "nothing was allowed".
    /// </returns>
    GovernanceTrace Snapshot();

    /// <summary>Clears the trail so the next turn starts clean.</summary>
    void Reset();

    /// <summary>
    /// Records that a gate outside the governor refused a call — either one the governor had already
    /// allowed, or one that never reached the governor at all.
    /// </summary>
    /// <param name="toolName">The tool that was stopped.</param>
    /// <param name="reason">Operator-facing explanation, for the trace and audit only.</param>
    /// <remarks>
    /// <para>
    /// The classification gate, the progress guard, the host's own <see cref="IToolCallObserver"/>
    /// rules, and the per-agent authorization gate ahead of the governor can each stop a call the
    /// governor never ruled on or had already permitted. Without this the trace would report such a
    /// call as <see cref="ToolDecisionOutcome.Allowed"/> or omit it entirely, and every consumer of the
    /// trace — bundle-run governance reporting, the dashboard, the audit — would be wrong for exactly
    /// the calls a safety rule stopped.
    /// </para>
    /// <para>
    /// When there is an earlier <see cref="ToolDecisionOutcome.Allowed"/> record for the same call, this
    /// does not revoke it; both are kept. The governor did allow it, something downstream did not, and
    /// an audit trail that shows only one of those facts is telling half the story.
    /// </para>
    /// <para>
    /// Only meaningful on an enforced turn: off that path nothing upstream recorded an allow, so there
    /// is no earlier decision to correct and no governance signal to add. <see cref="EnforcementEnabled"/>
    /// is what decides that, not whether the governor happened to run first — the per-agent
    /// authorization gate runs <em>before</em> the governor, so on the first tool call of a turn nothing
    /// has marked the turn enforced yet via <see cref="MarkEnforced"/>. Keying this on "the governor has
    /// already evaluated something" would silently drop every refusal that gate raises, which is most of
    /// them, since a call it refuses never reaches the governor at all.
    /// </para>
    /// <para>
    /// Classifies the tool's blast radius itself, from the same singleton risk classifier the governor
    /// uses, rather than asking the governor for one — the governor is not the only source of that
    /// classification, and requiring it here would mean standing one up just to correct a trace.
    /// </para>
    /// <para>
    /// Deliberately writes no audit entry: the caller has already audited its own refusal in its own
    /// vocabulary, and a second line here would make every downstream block count twice for anyone
    /// tallying denials from the audit stream.
    /// </para>
    /// </remarks>
    void RecordDownstreamBlock(string toolName, string reason);
}

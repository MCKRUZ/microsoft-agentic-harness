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
/// once for that call — the governor for its own verdicts, and
/// <see cref="IToolInvocationGovernor.RecordDownstreamBlock"/> on behalf of the stages that run before
/// and after it. A stage that writes a second line for a call another stage already recorded makes
/// every such call count twice for anyone tallying denials, which is why refusals are routed through
/// the governor rather than written here directly.
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
}

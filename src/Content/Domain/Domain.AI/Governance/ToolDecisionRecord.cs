using Domain.AI.Changes;

namespace Domain.AI.Governance;

/// <summary>
/// An immutable record of one governance decision taken when an agent attempted to
/// invoke a tool during a turn. Captured by <c>IToolInvocationGovernor</c> as the agent
/// runs and aggregated into a <see cref="GovernanceTrace"/>.
/// </summary>
/// <param name="ToolName">The tool the agent attempted to invoke.</param>
/// <param name="Outcome">The governance outcome for this invocation.</param>
/// <param name="Reason">Human-readable explanation of the decision (e.g. the matched rule or risk tier).</param>
/// <param name="BlastRadius">The tool's classified blast radius at decision time.</param>
/// <param name="RequiredApproval">True when the decision routed the call to a human approval gate.</param>
/// <param name="ApprovalGranted">True when an approval gate was encountered and a human approved the call.</param>
/// <param name="Enforced">
/// True when the decision was actually enforced (a denial blocked execution). False when the
/// governor ran in observe-only mode (enforcement disabled) and recorded the decision without
/// blocking — the signal that lets an eval distinguish governance from "narration".
/// </param>
public sealed record ToolDecisionRecord(
    string ToolName,
    ToolDecisionOutcome Outcome,
    string Reason,
    BlastRadius BlastRadius,
    bool RequiredApproval,
    bool ApprovalGranted,
    bool Enforced);

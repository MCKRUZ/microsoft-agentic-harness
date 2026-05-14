using Domain.AI.Governance;

namespace Application.Core.Workflows.Governance;

/// <summary>
/// Input to the governance approval workflow. Created when
/// <see cref="GovernancePolicyAction.RequireApproval"/> is returned by the policy engine.
/// </summary>
/// <param name="ToolName">The tool whose invocation requires approval.</param>
/// <param name="ToolArguments">Serialized tool arguments for auditing context.</param>
/// <param name="AgentId">The agent that requested the tool call.</param>
/// <param name="InitialDecision">The governance decision that triggered the approval flow.</param>
public sealed record GovernanceApprovalInput(
    string ToolName,
    string ToolArguments,
    string AgentId,
    GovernanceDecision InitialDecision);

/// <summary>
/// Human-readable approval request presented to the designated approver(s).
/// Produced by <see cref="CreateApprovalRequestExecutor"/> from the raw governance decision.
/// </summary>
/// <param name="ToolName">The tool requiring approval.</param>
/// <param name="AgentId">The agent that requested the tool call, for correlation across the port boundary.</param>
/// <param name="Description">Human-readable summary of what the tool call will do.</param>
/// <param name="Risk">Risk level or matched rule description from the governance policy.</param>
/// <param name="Approvers">Ordered list of designated approvers from the governance decision.</param>
/// <param name="RequestedAt">UTC timestamp when the approval was requested.</param>
public sealed record ApprovalRequest(
    string ToolName,
    string AgentId,
    string Description,
    string Risk,
    IReadOnlyList<string> Approvers,
    DateTimeOffset RequestedAt);

/// <summary>
/// Response from an approver to an <see cref="ApprovalRequest"/>.
/// Submitted externally via the workflow's <see cref="Microsoft.Agents.AI.Workflows.RequestPort"/>.
/// The <paramref name="AgentId"/> and <paramref name="ToolName"/> fields are echoed from the
/// original <see cref="ApprovalRequest"/> to maintain correlation across the port boundary.
/// </summary>
/// <param name="Approved">Whether the approver granted permission.</param>
/// <param name="ApproverName">Identity of the person who made the decision.</param>
/// <param name="Reason">Optional justification for the approval or denial.</param>
/// <param name="RespondedAt">UTC timestamp when the approval decision was made.</param>
/// <param name="AgentId">The agent that initiated the request, echoed from <see cref="ApprovalRequest"/>.</param>
/// <param name="ToolName">The tool that required approval, echoed from <see cref="ApprovalRequest"/>.</param>
public sealed record ApprovalResponse(
    bool Approved,
    string ApproverName,
    string? Reason,
    DateTimeOffset RespondedAt,
    string AgentId = "unknown",
    string ToolName = "unknown");

/// <summary>
/// Final outcome of the governance approval workflow. Contains the audit trail ID
/// for traceability and the original decision for correlation.
/// </summary>
/// <param name="Allowed">Whether the tool call is ultimately permitted after approval flow.</param>
/// <param name="AuditTrailId">Identifier linking this outcome to the governance audit chain.</param>
/// <param name="ApprovalResponse">The approver's response, or <c>null</c> if the workflow was short-circuited.</param>
/// <param name="OriginalDecision">
/// The governance decision that initiated the approval flow, or <c>null</c> when the decision
/// is unavailable after crossing the <see cref="Microsoft.Agents.AI.Workflows.RequestPort"/> boundary.
/// </param>
public sealed record GovernanceApprovalOutcome(
    bool Allowed,
    string AuditTrailId,
    ApprovalResponse? ApprovalResponse,
    GovernanceDecision? OriginalDecision);

using Application.AI.Common.Interfaces.Governance;
using Microsoft.Agents.AI.Workflows;

namespace Application.Core.Workflows.Governance;

/// <summary>
/// Records the approver's decision in the governance audit trail and produces the
/// final <see cref="GovernanceApprovalOutcome"/>. Last step in the governance approval workflow.
/// </summary>
/// <remarks>
/// Stateless executor safe for singleton registration. The <see cref="ApprovalResponse"/>
/// carries <see cref="ApprovalResponse.AgentId"/> and <see cref="ApprovalResponse.ToolName"/>
/// echoed from the original <see cref="ApprovalRequest"/>, providing correlation across
/// the <see cref="RequestPort"/> boundary without mutable instance state.
/// </remarks>
public sealed class ProcessApprovalOutcomeExecutor(
    IGovernanceAuditService auditService)
    : Executor<ApprovalResponse, GovernanceApprovalOutcome>("ProcessApprovalOutcome")
{
    /// <summary>
    /// Logs the approval decision to the audit chain and returns the final outcome.
    /// </summary>
    /// <param name="message">The approver's response with correlation fields.</param>
    /// <param name="context">The MAF workflow context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The final <see cref="GovernanceApprovalOutcome"/> with audit trail linkage.</returns>
    public override ValueTask<GovernanceApprovalOutcome> HandleAsync(
        ApprovalResponse message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var decision = message.Approved ? "Approved" : "Denied";
        var auditEntry = $"RequireApproval:{decision}|By:{message.ApproverName}|Reason:{message.Reason ?? "none"}";

        auditService.Log(message.AgentId, message.ToolName, auditEntry);

        var auditTrailId = $"gov-{Guid.NewGuid():N}";

        var outcome = new GovernanceApprovalOutcome(
            Allowed: message.Approved,
            AuditTrailId: auditTrailId,
            ApprovalResponse: message,
            OriginalDecision: null);

        return ValueTask.FromResult(outcome);
    }
}

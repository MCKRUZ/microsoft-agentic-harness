using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Redaction;
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
/// <para>
/// The governance trail is hash-chained and cannot be edited, so the approver is recorded as a keyed reference
/// (<see cref="IAuditPseudonymizer.Person"/>) and the reason with identifiers redacted (#903). The reason is redacted
/// before the entry is put together, so the redaction never touches the reference.
/// </para>
/// </remarks>
public sealed class ProcessApprovalOutcomeExecutor(
    IGovernanceAuditService auditService,
    IAuditPseudonymizer pseudonymizer,
    IContentRedactionFilter redaction)
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
        var reason = message.Reason is null ? "none" : redaction.Redact(message.Reason, RedactionCategories.All);
        var auditEntry = $"RequireApproval:{decision}|By:{pseudonymizer.Person(message.ApproverName)}|Reason:{reason}";

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

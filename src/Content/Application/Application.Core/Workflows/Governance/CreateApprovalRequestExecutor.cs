using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Escalation;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace Application.Core.Workflows.Governance;

/// <summary>
/// Transforms a <see cref="GovernanceApprovalInput"/> into a human-readable
/// <see cref="ApprovalRequest"/> and logs the pending approval to the governance audit chain.
/// When an <see cref="IEscalationService"/> is wired, also queues an escalation request
/// to start the notification dispatch and timeout timer.
/// First step in the governance approval workflow.
/// </summary>
public sealed class CreateApprovalRequestExecutor(
    IGovernanceAuditService auditService,
    ILogger<CreateApprovalRequestExecutor> logger,
    IEscalationService? escalationService = null)
    : Executor<GovernanceApprovalInput, ApprovalRequest>("CreateApprovalRequest")
{
    /// <summary>
    /// Builds an <see cref="ApprovalRequest"/> from the governance decision metadata
    /// and records the pending approval in the audit trail. When escalation is available,
    /// queues a non-blocking escalation to start notification dispatch.
    /// </summary>
    /// <param name="message">The governance input containing the tool call details and initial decision.</param>
    /// <param name="context">The MAF workflow context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="ApprovalRequest"/> ready for human review.</returns>
    public override async ValueTask<ApprovalRequest> HandleAsync(
        GovernanceApprovalInput message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var decision = message.InitialDecision;

        auditService.Log(
            message.AgentId,
            message.ToolName,
            $"RequireApproval:Pending|Rule:{decision.MatchedRule ?? "none"}|Policy:{decision.PolicyName ?? "none"}");

        var request = new ApprovalRequest(
            ToolName: message.ToolName,
            AgentId: message.AgentId,
            Description: $"Agent '{message.AgentId}' requests to invoke tool '{message.ToolName}' with arguments: {message.ToolArguments}",
            Risk: decision.MatchedRule ?? decision.Reason,
            Approvers: decision.Approvers ?? [],
            RequestedAt: DateTimeOffset.UtcNow);

        if (escalationService is not null)
        {
            var escalationRequest = new EscalationRequest
            {
                EscalationId = Guid.NewGuid(),
                AgentId = message.AgentId,
                ToolName = message.ToolName,
                Arguments = new Dictionary<string, string>(),
                Description = request.Description,
                RiskLevel = RiskLevel.Medium,
                Priority = EscalationPriority.Blocking,
                Approvers = decision.Approvers ?? [],
                RequestedAt = request.RequestedAt,
                OriginatingDecision = decision
            };

            try
            {
                await escalationService.QueueEscalationAsync(escalationRequest, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to queue escalation for agent {AgentId} tool {ToolName} — approval request still returned",
                    message.AgentId, message.ToolName);
            }
        }

        return request;
    }
}

using Application.AI.Common.Interfaces.Governance;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.Core.Workflows.Governance;

/// <summary>
/// Static factory for building the governance human-in-the-loop approval workflow.
/// Uses MAF's <see cref="RequestPort"/> to pause execution while waiting for an external
/// approver to submit an <see cref="ApprovalResponse"/>.
/// </summary>
/// <remarks>
/// <para>
/// Workflow graph: <c>CreateApprovalRequest</c> --> <c>[GovernanceApproval Port]</c> --> <c>ProcessApprovalOutcome</c>
/// </para>
/// <para>
/// The returned <see cref="RequestPort"/> must be retained by the caller so that approval
/// responses can be submitted at runtime via the workflow session.
/// </para>
/// </remarks>
public static class GovernanceApprovalWorkflow
{
    /// <summary>
    /// Builds the governance approval workflow and its associated request port.
    /// </summary>
    /// <param name="services">
    /// Service provider used to resolve <see cref="IGovernanceAuditService"/>.
    /// </param>
    /// <returns>
    /// A tuple containing the built <see cref="Workflow"/> and the
    /// <see cref="RequestPort"/> through which approval responses are submitted.
    /// </returns>
    public static (Workflow Workflow, RequestPort ApprovalPort) Build(IServiceProvider services)
    {
        var auditService = services.GetRequiredService<IGovernanceAuditService>();
        var logger = services.GetRequiredService<ILogger<CreateApprovalRequestExecutor>>();

        var createRequest = new CreateApprovalRequestExecutor(auditService, logger);
        var approvalPort = RequestPort.Create<ApprovalRequest, ApprovalResponse>("GovernanceApproval");
        var processOutcome = new ProcessApprovalOutcomeExecutor(auditService);

        var workflow = new WorkflowBuilder(createRequest)
            .WithName("GovernanceApproval")
            .WithDescription("Human-in-the-loop approval for governed tool calls")
            .AddEdge(createRequest, approvalPort)
            .AddEdge(approvalPort, processOutcome)
            .WithOutputFrom(processOutcome)
            .Build();

        return (workflow, approvalPort);
    }
}

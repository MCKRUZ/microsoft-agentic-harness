using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.Core.Workflows.Governance;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.Core.Tests.Workflows.Governance;

/// <summary>
/// Tests for <see cref="CreateApprovalRequestExecutor"/> verifying that it delegates
/// to <see cref="IEscalationService"/> for notification dispatch and timeout tracking
/// while continuing to produce the workflow <see cref="ApprovalRequest"/>.
/// </summary>
public sealed class CreateApprovalRequestExecutorTests
{
    private readonly Mock<IGovernanceAuditService> _auditService = new();
    private readonly Mock<IEscalationService> _escalationService = new();
    private readonly Mock<ILogger<CreateApprovalRequestExecutor>> _logger = new();
    private readonly Mock<IWorkflowContext> _workflowContext = new();

    [Fact]
    public async Task HandleAsync_DelegatesToEscalationService()
    {
        var escalationId = Guid.NewGuid();
        _escalationService
            .Setup(x => x.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(escalationId);

        var decision = new GovernanceDecision(
            false, GovernancePolicyAction.RequireApproval,
            "Requires approval", "high-risk", "security",
            Approvers: ["admin@test.com"]);

        var input = new GovernanceApprovalInput(
            ToolName: "deploy",
            ToolArguments: "{}",
            AgentId: "test-agent",
            InitialDecision: decision);

        var executor = new CreateApprovalRequestExecutor(
            _auditService.Object,
            _logger.Object,
            _escalationService.Object);

        var result = await executor.HandleAsync(input, _workflowContext.Object, CancellationToken.None);

        Assert.Equal("deploy", result.ToolName);
        Assert.Equal("test-agent", result.AgentId);

        _escalationService.Verify(
            x => x.QueueEscalationAsync(
                It.Is<EscalationRequest>(r =>
                    r.AgentId == "test-agent" &&
                    r.ToolName == "deploy"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_StillReturnsApprovalRequest_WhenEscalationQueued()
    {
        _escalationService
            .Setup(x => x.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        var decision = new GovernanceDecision(
            false, GovernancePolicyAction.RequireApproval,
            "Requires approval", "rule-1", "policy-1",
            Approvers: ["approver1"]);

        var input = new GovernanceApprovalInput(
            ToolName: "execute",
            ToolArguments: "{\"cmd\":\"deploy\"}",
            AgentId: "agent-1",
            InitialDecision: decision);

        var executor = new CreateApprovalRequestExecutor(
            _auditService.Object,
            _logger.Object,
            _escalationService.Object);

        var result = await executor.HandleAsync(input, _workflowContext.Object, CancellationToken.None);

        Assert.Equal("execute", result.ToolName);
        Assert.Equal("agent-1", result.AgentId);
        Assert.Contains("approver1", result.Approvers);
    }

    [Fact]
    public async Task HandleAsync_NoEscalationService_StillReturnsApprovalRequest()
    {
        var decision = new GovernanceDecision(
            false, GovernancePolicyAction.RequireApproval,
            "Requires approval", "rule-1", "policy-1",
            Approvers: ["admin"]);

        var input = new GovernanceApprovalInput(
            ToolName: "write_file",
            ToolArguments: "{}",
            AgentId: "agent-2",
            InitialDecision: decision);

        var executor = new CreateApprovalRequestExecutor(
            _auditService.Object,
            _logger.Object,
            escalationService: null);

        var result = await executor.HandleAsync(input, _workflowContext.Object, CancellationToken.None);

        Assert.Equal("write_file", result.ToolName);
        Assert.Equal("agent-2", result.AgentId);
    }
}

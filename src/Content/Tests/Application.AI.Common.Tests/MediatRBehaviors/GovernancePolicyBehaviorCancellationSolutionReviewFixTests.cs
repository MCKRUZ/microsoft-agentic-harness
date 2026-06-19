using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.MediatRBehaviors;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.Common;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using Domain.Common.Config.AI.Permissions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

/// <summary>
/// Regression tests for the solution-review finding that
/// <see cref="GovernancePolicyBehavior{TRequest, TResponse}"/> swallowed
/// <see cref="OperationCanceledException"/> thrown from the escalation wait, converting
/// cooperative caller cancellation into a <c>GovernanceBlocked</c> result instead of
/// propagating it. Cancellation must surface to the caller, matching every other behavior
/// and handler in this assembly that explicitly rethrows on cancellation.
/// </summary>
public sealed class GovernancePolicyBehaviorCancellationSolutionReviewFixTests
{
    private readonly Mock<IGovernancePolicyEngine> _policyEngine = new();
    private readonly Mock<IGovernanceAuditService> _auditService = new();
    private readonly Mock<IAgentExecutionContext> _executionContext = new();
    private readonly Mock<IEscalationService> _escalationService = new();
    private readonly Mock<ILogger<GovernancePolicyBehavior<CancellableToolRequest, Result<string>>>> _logger = new();
    private readonly GovernanceConfig _config;
    private readonly PermissionsConfig _permissionsConfig;
    private bool _nextCalled;

    public GovernancePolicyBehaviorCancellationSolutionReviewFixTests()
    {
        _config = new GovernanceConfig
        {
            Enabled = true,
            EnableAudit = true,
            Escalation = new EscalationConfig
            {
                Enabled = true,
                DefaultTimeoutSeconds = 60,
                DefaultTimeoutAction = "DenyAndEscalate",
                DefaultApprovalStrategy = "AnyOf"
            }
        };

        _permissionsConfig = new PermissionsConfig
        {
            TierPolicies = new Dictionary<string, AutonomyTierPolicyConfig>
            {
                ["Supervised"] = new() { EscalationBehavior = "Block" }
            }
        };

        _executionContext.Setup(x => x.AgentId).Returns("test-agent");
    }

    private GovernancePolicyBehavior<CancellableToolRequest, Result<string>> CreateBehavior() =>
        new(
            _policyEngine.Object,
            _auditService.Object,
            _executionContext.Object,
            Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == _config),
            Mock.Of<IOptionsMonitor<PermissionsConfig>>(m => m.CurrentValue == _permissionsConfig),
            Mock.Of<Application.AI.Common.Interfaces.Tools.IToolRiskClassifier>(
                c => c.Classify(It.IsAny<string>()) == Application.AI.Common.Interfaces.Tools.ToolRiskProfile.Default),
            _logger.Object,
            _escalationService.Object);

    private Task<Result<string>> Next()
    {
        _nextCalled = true;
        return Task.FromResult(Result<string>.Success("ok"));
    }

    private static GovernanceDecision RequireApprovalDecision() =>
        new(false, GovernancePolicyAction.RequireApproval, "Requires approval",
            "high-risk-tool", "security-policy", Approvers: ["admin@test.com"]);

    [Fact]
    public async Task Handle_EscalationWaitCancelled_PropagatesOperationCanceledException()
    {
        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "deploy", null))
            .Returns(RequireApprovalDecision());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _escalationService
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var behavior = CreateBehavior();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => behavior.Handle(new CancellableToolRequest("deploy"), Next, cts.Token));

        Assert.False(_nextCalled);
    }

    [Fact]
    public async Task Handle_EscalationServiceThrowsTaskCanceled_PropagatesCancellation()
    {
        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "deploy", null))
            .Returns(RequireApprovalDecision());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // TaskCanceledException derives from OperationCanceledException and must also propagate.
        _escalationService
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException());

        var behavior = CreateBehavior();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => behavior.Handle(new CancellableToolRequest("deploy"), Next, cts.Token));
    }

    [Fact]
    public async Task Handle_EscalationServiceThrowsNonCancellation_StillFailsClosed()
    {
        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "deploy", null))
            .Returns(RequireApprovalDecision());

        _escalationService
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Service unavailable"));

        var behavior = CreateBehavior();
        var result = await behavior.Handle(new CancellableToolRequest("deploy"), Next, CancellationToken.None);

        Assert.False(_nextCalled);
        Assert.False(result.IsSuccess);
        Assert.Equal(ResultFailureType.GovernanceBlocked, result.FailureType);
    }

    public sealed record CancellableToolRequest(string ToolName) : IToolRequest;
}

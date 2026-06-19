using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.MediatRBehaviors;
using Domain.AI.Changes;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.Common;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using Domain.Common.Config.AI.Permissions;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

/// <summary>
/// Tests for the <see cref="GovernancePolicyBehavior{TRequest, TResponse}"/> escalation
/// integration. Validates RequireApproval decisions are routed through <see cref="IEscalationService"/>
/// with correct blocking/non-blocking behavior.
/// </summary>
public sealed class GovernancePolicyBehaviorEscalationTests
{
    private readonly Mock<IGovernancePolicyEngine> _policyEngine = new();
    private readonly Mock<IGovernanceAuditService> _auditService = new();
    private readonly Mock<IAgentExecutionContext> _executionContext = new();
    private readonly Mock<IEscalationService> _escalationService = new();
    private readonly Mock<ILogger<GovernancePolicyBehavior<TestToolRequest, Result<string>>>> _logger = new();
    private readonly GovernanceConfig _config;
    private readonly PermissionsConfig _permissionsConfig;
    private IToolRiskClassifier _toolRiskClassifier =
        Mock.Of<IToolRiskClassifier>(c => c.Classify(It.IsAny<string>()) == ToolRiskProfile.Default);
    private bool _nextCalled;

    public GovernancePolicyBehaviorEscalationTests()
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
                ["Supervised"] = new() { EscalationBehavior = "Block" },
                ["Autonomous"] = new() { EscalationBehavior = "QueueAndContinue" }
            }
        };

        _executionContext.Setup(x => x.AgentId).Returns("test-agent");
    }

    private GovernancePolicyBehavior<TestToolRequest, Result<string>> CreateBehavior(
        GovernanceConfig? configOverride = null,
        PermissionsConfig? permissionsOverride = null)
    {
        var cfg = configOverride ?? _config;
        var perm = permissionsOverride ?? _permissionsConfig;
        return new GovernancePolicyBehavior<TestToolRequest, Result<string>>(
            _policyEngine.Object,
            _auditService.Object,
            _executionContext.Object,
            Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == cfg),
            Mock.Of<IOptionsMonitor<PermissionsConfig>>(m => m.CurrentValue == perm),
            _toolRiskClassifier,
            _logger.Object,
            _escalationService.Object);
    }

    private Task<Result<string>> Next()
    {
        _nextCalled = true;
        return Task.FromResult(Result<string>.Success("ok"));
    }

    private static GovernanceDecision RequireApprovalDecision(
        string rule = "high-risk-tool",
        string policy = "security-policy",
        string reason = "Requires approval",
        IReadOnlyList<string>? approvers = null) =>
        new(false, GovernancePolicyAction.RequireApproval, reason, rule, policy,
            Approvers: approvers ?? ["admin@test.com"]);

    [Fact]
    public async Task Handle_RequireApprovalBlocking_CallsEscalationService()
    {
        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "deploy", null))
            .Returns(RequireApprovalDecision());

        _escalationService
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EscalationOutcome
            {
                EscalationId = Guid.NewGuid(),
                IsApproved = true,
                Decisions = [],
                ResolutionType = EscalationResolutionType.Approved,
                ResolvedAt = DateTimeOffset.UtcNow
            });

        var behavior = CreateBehavior();
        await behavior.Handle(new TestToolRequest("deploy"), Next, CancellationToken.None);

        _escalationService.Verify(
            x => x.RequestEscalationAsync(
                It.Is<EscalationRequest>(r =>
                    r.AgentId == "test-agent" &&
                    r.ToolName == "deploy" &&
                    r.Approvers.Contains("admin@test.com")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_RequireApproval_DerivesEscalationRiskLevelFromToolBlastRadius()
    {
        // A High-blast-radius tool must escalate as High, not the previously-hardcoded Medium.
        _toolRiskClassifier = Mock.Of<IToolRiskClassifier>(
            c => c.Classify("deploy") == new ToolRiskProfile(BlastRadius.High, false));
        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "deploy", null))
            .Returns(RequireApprovalDecision());
        _escalationService
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EscalationOutcome
            {
                EscalationId = Guid.NewGuid(),
                IsApproved = true,
                Decisions = [],
                ResolutionType = EscalationResolutionType.Approved,
                ResolvedAt = DateTimeOffset.UtcNow
            });

        await CreateBehavior().Handle(new TestToolRequest("deploy"), Next, CancellationToken.None);

        _escalationService.Verify(
            x => x.RequestEscalationAsync(
                It.Is<EscalationRequest>(r => r.RiskLevel == RiskLevel.High),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_RequireApprovalBlocking_Approved_ProceedsWithNext()
    {
        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "deploy", null))
            .Returns(RequireApprovalDecision());

        _escalationService
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EscalationOutcome
            {
                EscalationId = Guid.NewGuid(),
                IsApproved = true,
                Decisions = [],
                ResolutionType = EscalationResolutionType.Approved,
                ResolvedAt = DateTimeOffset.UtcNow
            });

        var behavior = CreateBehavior();
        var result = await behavior.Handle(new TestToolRequest("deploy"), Next, CancellationToken.None);

        Assert.True(_nextCalled);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Handle_RequireApprovalBlocking_Denied_ReturnsDeniedResult()
    {
        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "deploy", null))
            .Returns(RequireApprovalDecision());

        _escalationService
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EscalationOutcome
            {
                EscalationId = Guid.NewGuid(),
                IsApproved = false,
                Decisions = [],
                ResolutionType = EscalationResolutionType.Denied,
                ResolvedAt = DateTimeOffset.UtcNow
            });

        var behavior = CreateBehavior();
        var result = await behavior.Handle(new TestToolRequest("deploy"), Next, CancellationToken.None);

        Assert.False(_nextCalled);
        Assert.False(result.IsSuccess);
        Assert.Equal(ResultFailureType.GovernanceBlocked, result.FailureType);
    }

    [Fact]
    public async Task Handle_RequireApprovalQueueAndContinue_ReturnsPendingResult()
    {
        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "deploy", null))
            .Returns(RequireApprovalDecision());

        var escalationId = Guid.NewGuid();
        _escalationService
            .Setup(x => x.QueueEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(escalationId);

        // Set tier to Autonomous which uses QueueAndContinue
        var permConfig = new PermissionsConfig
        {
            TierPolicies = new Dictionary<string, AutonomyTierPolicyConfig>
            {
                ["Supervised"] = new() { EscalationBehavior = "QueueAndContinue" }
            }
        };

        var behavior = CreateBehavior(permissionsOverride: permConfig);
        var result = await behavior.Handle(new TestToolRequest("deploy"), Next, CancellationToken.None);

        Assert.False(_nextCalled);
        Assert.False(result.IsSuccess);
        Assert.Equal(ResultFailureType.PendingApproval, result.FailureType);
        Assert.Contains(escalationId.ToString(), result.Errors[0]);
    }

    [Fact]
    public async Task Handle_RequireApproval_EscalationDisabled_FallsThrough()
    {
        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "deploy", null))
            .Returns(RequireApprovalDecision());

        var disabledConfig = new GovernanceConfig
        {
            Enabled = true,
            EnableAudit = true,
            Escalation = new EscalationConfig { Enabled = false }
        };

        var behavior = CreateBehavior(configOverride: disabledConfig);
        var result = await behavior.Handle(new TestToolRequest("deploy"), Next, CancellationToken.None);

        Assert.False(_nextCalled);
        Assert.Equal(ResultFailureType.GovernanceBlocked, result.FailureType);
        _escalationService.Verify(
            x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_RequireApproval_EscalationServiceThrows_ReturnsGovernanceBlocked()
    {
        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "deploy", null))
            .Returns(RequireApprovalDecision());

        _escalationService
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Service unavailable"));

        var behavior = CreateBehavior();
        var result = await behavior.Handle(new TestToolRequest("deploy"), Next, CancellationToken.None);

        Assert.False(_nextCalled);
        Assert.False(result.IsSuccess);
        Assert.Equal(ResultFailureType.GovernanceBlocked, result.FailureType);
    }

    public sealed record TestToolRequest(string ToolName) : IToolRequest;
}

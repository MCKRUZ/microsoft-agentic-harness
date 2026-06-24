using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.Common;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Permissions;
using Domain.Common.Config.AI.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Verifies the per-invocation tool governor enforces the permission / graded-autonomy /
/// capability / policy gate on the live tool path and records an accurate governance trace.
/// </summary>
public sealed class ToolInvocationGovernorTests
{
    private const string Agent = "test-agent";
    private const string Tool = "file_system";

    private readonly Mock<IAgentExecutionContext> _context = new();
    private readonly Mock<IToolPermissionService> _permissions = new();
    private readonly Mock<IAutonomyDecisionEvaluator> _autonomy = new();
    private readonly Mock<IGovernancePolicyEngine> _policyEngine = new();
    private readonly Mock<IDenialTracker> _denialTracker = new();
    private readonly Mock<ICapabilityEnforcer> _capabilities = new();
    private readonly IToolRiskClassifier _riskClassifier =
        Mock.Of<IToolRiskClassifier>(c => c.Classify(It.IsAny<string>()) == new ToolRiskProfile(BlastRadius.Low, true));

    private readonly GovernanceConfig _governance = new() { EnforceToolInvocation = true, Enabled = false, EnableAudit = true };
    private readonly PermissionsConfig _permissionsConfig = new();
    private readonly SandboxConfig _sandbox = new();

    public ToolInvocationGovernorTests()
    {
        _context.Setup(x => x.AgentId).Returns(Agent);
        _permissions
            .Setup(x => x.ResolvePermissionAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionDecision.Allow("allowed by default"));
        _capabilities
            .Setup(x => x.EnforceAsync(It.IsAny<string>(), It.IsAny<Domain.AI.Sandbox.ToolCapability>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _policyEngine.SetupGet(x => x.HasPolicies).Returns(false);
    }

    private ToolInvocationGovernor Build() => new(
        _context.Object,
        _permissions.Object,
        _riskClassifier,
        _autonomy.Object,
        _policyEngine.Object,
        Mock.Of<IGovernanceAuditService>(),
        _denialTracker.Object,
        _capabilities.Object,
        Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == _governance),
        Mock.Of<IOptionsMonitor<PermissionsConfig>>(m => m.CurrentValue == _permissionsConfig),
        Mock.Of<IOptionsMonitor<SandboxConfig>>(m => m.CurrentValue == _sandbox),
        NullLogger<ToolInvocationGovernor>.Instance);

    [Fact]
    public async Task AuthorizeAsync_EnforcementDisabled_AllowsAndDoesNotEvaluate()
    {
        var governance = new GovernanceConfig { EnforceToolInvocation = false };
        var governor = new ToolInvocationGovernor(
            _context.Object, _permissions.Object, _riskClassifier, _autonomy.Object, _policyEngine.Object,
            Mock.Of<IGovernanceAuditService>(), _denialTracker.Object, _capabilities.Object,
            Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == governance),
            Mock.Of<IOptionsMonitor<PermissionsConfig>>(m => m.CurrentValue == _permissionsConfig),
            Mock.Of<IOptionsMonitor<SandboxConfig>>(m => m.CurrentValue == _sandbox),
            NullLogger<ToolInvocationGovernor>.Instance);

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.True(decision.IsAllowed);
        Assert.Same(GovernanceTrace.Empty, governor.GetTrace());
        _permissions.Verify(x => x.ResolvePermissionAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AuthorizeAsync_NoAgentId_Allows()
    {
        _context.Setup(x => x.AgentId).Returns((string?)null);
        var governor = Build();

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task AuthorizeAsync_PermissionAllow_AllowsAndRecordsAllowed()
    {
        var governor = Build();

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.True(decision.IsAllowed);
        var trace = governor.GetTrace();
        Assert.True(trace.EnforcementEnabled);
        var record = Assert.Single(trace.ToolDecisions);
        Assert.Equal(ToolDecisionOutcome.Allowed, record.Outcome);
        Assert.Equal(1, trace.AllowedCount);
    }

    [Fact]
    public async Task AuthorizeAsync_PermissionDeny_BlocksAndRecordsDenial()
    {
        _permissions
            .Setup(x => x.ResolvePermissionAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionDecision.Deny("not allowed for this agent"));
        var governor = Build();

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.NotNull(decision.DeniedMessage);
        var record = Assert.Single(governor.GetTrace().ToolDecisions);
        Assert.Equal(ToolDecisionOutcome.Denied, record.Outcome);
        Assert.True(record.Enforced);
        _denialTracker.Verify(x => x.RecordDenial(Agent, Tool, null), Times.Once);
    }

    [Fact]
    public async Task AuthorizeAsync_PermissionAsk_BlocksAndRecordsPendingApproval()
    {
        _permissions
            .Setup(x => x.ResolvePermissionAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionDecision.Ask("needs human sign-off"));
        var governor = Build();

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        var trace = governor.GetTrace();
        var record = Assert.Single(trace.ToolDecisions);
        Assert.Equal(ToolDecisionOutcome.PendingApproval, record.Outcome);
        Assert.True(record.RequiredApproval);
        Assert.True(trace.ApprovalGateEncountered);
        Assert.False(trace.ApprovalBypassed); // gate enforced — not bypassed
    }

    [Fact]
    public async Task AuthorizeAsync_GradedAutonomyTightensAllowToApproval_Blocks()
    {
        _permissionsConfig.GradedAutonomy.Enabled = true;
        _permissionsConfig.DefaultAutonomyLevel = "Supervised";
        _autonomy
            .Setup(x => x.Evaluate(It.IsAny<AutonomyLevel>(), It.IsAny<BlastRadius>(),
                It.IsAny<ChangeTargetKind>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .Returns(new AutonomyDecisionResult(
                AutonomyDecision.RequiresApproval, AutonomyLevel.Supervised, BlastRadius.Low,
                ChangeTargetKind.Unspecified, IsStateChange: true, "Development", null, "tier requires approval"));
        var governor = Build();

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        var record = Assert.Single(governor.GetTrace().ToolDecisions);
        Assert.Equal(ToolDecisionOutcome.PendingApproval, record.Outcome);
        Assert.True(record.RequiredApproval);
    }

    [Fact]
    public async Task AuthorizeAsync_CapabilityViolation_Blocks()
    {
        _capabilities
            .Setup(x => x.EnforceAsync(It.IsAny<string>(), It.IsAny<Domain.AI.Sandbox.ToolCapability>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Fail("filesystem capability not granted"));
        var governor = Build();

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        var record = Assert.Single(governor.GetTrace().ToolDecisions);
        Assert.Equal(ToolDecisionOutcome.Denied, record.Outcome);
        Assert.Contains("capability", record.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reset_ClearsPriorTurnDecisions_NoCrossTurnDoubleCount()
    {
        // The governor is scoped but shared across turns of a conversation (nested MediatR sends
        // share one DI scope), so each turn must Reset() or the trace accumulates and the merged
        // conversation trace double-counts. This guards that regression.
        var governor = Build();

        // Turn 1
        await governor.AuthorizeAsync(Tool, CancellationToken.None);
        Assert.Equal(1, governor.GetTrace().ToolInvocationCount);

        // Turn 2 begins
        governor.Reset();
        await governor.AuthorizeAsync(Tool, CancellationToken.None);

        var trace = governor.GetTrace();
        Assert.Equal(1, trace.ToolInvocationCount); // this turn only, not cumulative
        Assert.Equal(1, trace.AllowedCount);
    }

    [Fact]
    public async Task AuthorizeAsync_PolicyEngineDenies_Blocks()
    {
        // GovernanceConfig is init-only — build one with the policy layer enabled.
        var governance = new GovernanceConfig { EnforceToolInvocation = true, Enabled = true, EnableAudit = true };
        _policyEngine.SetupGet(x => x.HasPolicies).Returns(true);
        _policyEngine
            .Setup(x => x.EvaluateToolCall(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object>?>()))
            .Returns(GovernanceDecision.Denied("rule-7", "default-policy", "blocked by policy"));

        var governor = new ToolInvocationGovernor(
            _context.Object, _permissions.Object, _riskClassifier, _autonomy.Object, _policyEngine.Object,
            Mock.Of<IGovernanceAuditService>(), _denialTracker.Object, _capabilities.Object,
            Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == governance),
            Mock.Of<IOptionsMonitor<PermissionsConfig>>(m => m.CurrentValue == _permissionsConfig),
            Mock.Of<IOptionsMonitor<SandboxConfig>>(m => m.CurrentValue == _sandbox),
            NullLogger<ToolInvocationGovernor>.Instance);

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(ToolDecisionOutcome.Denied, Assert.Single(governor.GetTrace().ToolDecisions).Outcome);
    }
}

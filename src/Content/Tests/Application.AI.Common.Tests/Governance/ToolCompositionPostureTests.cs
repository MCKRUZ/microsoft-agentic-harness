using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Tools;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using Domain.Common.Config.AI.Permissions;
using Domain.Common.Config.AI.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// The tool-composition RequireApproval posture, exercised through the real admission chain — the same
/// pattern <see cref="ToolBehaviorPostureTests"/> uses for #324, applied to #332.
/// </summary>
public sealed class ToolCompositionPostureTests
{
    private const string Agent = "test-agent";
    private const string SinkTool = "send_email";

    private readonly Mock<IAgentExecutionContext> _context = new();
    private readonly Mock<IToolPermissionService> _permissions = new();
    private readonly Mock<IGovernancePolicyEngine> _policyEngine = new();
    private readonly Mock<ICapabilityEnforcer> _capabilities = new();
    private readonly Mock<IToolApprovalRouter> _approvalRouter = new();
    private readonly ToolBehaviorRegistry _behavior = new(new ServiceCollection().BuildServiceProvider());

    private readonly IToolRiskClassifier _riskClassifier =
        Mock.Of<IToolRiskClassifier>(c =>
            c.Classify(It.IsAny<string>()) == new ToolRiskProfile(BlastRadius.Low, true));

    public ToolCompositionPostureTests()
    {
        _context.Setup(x => x.AgentId).Returns(Agent);
        _permissions
            .Setup(x => x.ResolvePermissionAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionDecision.Allow("allowed by default"));
        _capabilities
            .Setup(x => x.EnforceAsync(It.IsAny<string>(), It.IsAny<Domain.AI.Sandbox.ToolCapability>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result.Success());
        _policyEngine.SetupGet(x => x.HasPolicies).Returns(false);
        _approvalRouter
            .Setup(x => x.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolApprovalResult.NotRouted("tool approval routing is disabled"));
    }

    [Fact]
    public async Task RequireApprovalPairing_TaintedSink_IsGatedNamingThePath()
    {
        var taint = new ToolCompositionTaint([Finding("web_fetch", ToolCompositionCapability.IngestsUntrustedInput)]);

        var admission = await Admit(RequireApprovalGating, taint);

        Assert.False(admission.IsAllowed);
        AssertApprovalWasSought("web_fetch");
        AssertApprovalWasSought(SinkTool);
    }

    [Fact]
    public async Task AllowPairing_TaintedSink_RunsWithoutApproval()
    {
        // The control for the test above: the SAME taint under a posture that has not opted this
        // pairing into RequireApproval must not gate anything. Config with nothing set is off.
        var taint = new ToolCompositionTaint([Finding("web_fetch", ToolCompositionCapability.IngestsUntrustedInput)]);

        var admission = await Admit(new GovernanceConfig { EnforceToolInvocation = true }, taint);

        Assert.True(admission.IsAllowed);
        AssertNoApprovalWasSought();
    }

    [Fact]
    public async Task WarnPairing_TaintedSink_RunsWithoutApproval()
    {
        // Warn reports (elsewhere, via ToolCompositionReporter) but does not gate at call time.
        var gating = new ToolCompositionGatingConfig
        {
            Pairings =
            [
                new ToolCompositionPairing
                {
                    Source = ToolCompositionCapability.IngestsUntrustedInput,
                    Sink = ToolCompositionCapability.SendsOutbound,
                    Posture = CompositionPosture.Warn,
                },
            ],
        };
        var taint = new ToolCompositionTaint([Finding("web_fetch", ToolCompositionCapability.IngestsUntrustedInput)]);

        var admission = await Admit(Enforcing(gating), taint);

        Assert.True(admission.IsAllowed);
        AssertNoApprovalWasSought();
    }

    [Fact]
    public async Task NoTaint_RequireApprovalConfigured_RunsWithoutApproval()
    {
        // No finding was stamped for this call — a plan step, or a build whose analysis found nothing
        // for this tool. Null composition reads identically to "no findings", never as "unknown".
        var admission = await Admit(RequireApprovalGating, composition: null);

        Assert.True(admission.IsAllowed);
        AssertNoApprovalWasSought();
    }

    [Fact]
    public async Task RequireApprovalPairing_LiveConfigChange_TakesEffectWithoutRebuildingTheTaint()
    {
        // The whole point of NOT freezing a posture into the finding: the identical ToolCompositionTaint
        // instance (the "build") enforces differently as the config (read live) changes underneath it.
        var taint = new ToolCompositionTaint([Finding("web_fetch", ToolCompositionCapability.IngestsUntrustedInput)]);

        var beforeChange = await Admit(new GovernanceConfig { EnforceToolInvocation = true }, taint);
        Assert.True(beforeChange.IsAllowed);

        var afterChange = await Admit(RequireApprovalGating, taint);
        Assert.False(afterChange.IsAllowed);
    }

    [Fact]
    public async Task BehaviorGatingAndCompositionBothObject_RaiseExactlyOneApprovalRequest()
    {
        // The property #324's doc calls out explicitly: two independent reasons to ask a human must
        // still produce ONE question, or an approver clears a question they were never shown.
        _behavior.RecordAdvertised(SinkTool, new ToolBehavior(ToolBehaviorSource.UntrustedMcpServer, Destructive: true, ServerName: "email"));

        var gating = new GovernanceConfig
        {
            EnforceToolInvocation = true,
            EnableAudit = true,
            ToolCompositionGating = RequireApprovalGating.ToolCompositionGating,
            ToolBehaviorGating = new ToolBehaviorGatingConfig { RequireApprovalForNonReadOnlyTools = true },
        };
        var taint = new ToolCompositionTaint([Finding("web_fetch", ToolCompositionCapability.IngestsUntrustedInput)]);

        var admission = await Admit(gating, taint);

        Assert.False(admission.IsAllowed);
        _approvalRouter.Verify(
            x => x.RequestApprovalAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static ToolCompositionFinding Finding(string sourceTool, ToolCompositionCapability sourceCapability) =>
        new(sourceTool, sourceCapability, SinkTool, ToolCompositionCapability.SendsOutbound,
            ToolCapabilityOrigin.KeywordHeuristic, ToolCapabilityOrigin.KeywordHeuristic);

    private static GovernanceConfig Enforcing(ToolCompositionGatingConfig gating) => new()
    {
        EnforceToolInvocation = true,
        EnableAudit = true,
        ToolCompositionGating = gating,
    };

    private static GovernanceConfig RequireApprovalGating => Enforcing(new ToolCompositionGatingConfig
    {
        Pairings =
        [
            new ToolCompositionPairing
            {
                Source = ToolCompositionCapability.IngestsUntrustedInput,
                Sink = ToolCompositionCapability.SendsOutbound,
                Posture = CompositionPosture.RequireApproval,
            },
        ],
    });

    private async Task<ToolCallAdmission> Admit(GovernanceConfig governance, ToolCompositionTaint? composition)
    {
        var monitor = Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == governance);
        var trace = new GovernanceTraceRecorder(monitor, _riskClassifier);

        var governor = new ToolInvocationGovernor(
            _context.Object,
            _permissions.Object,
            _riskClassifier,
            _behavior,
            Mock.Of<IAutonomyDecisionEvaluator>(),
            _policyEngine.Object,
            Mock.Of<IGovernanceAuditService>(),
            Mock.Of<IDenialTracker>(),
            _capabilities.Object,
            _approvalRouter.Object,
            trace,
            monitor,
            Mock.Of<IOptionsMonitor<PermissionsConfig>>(m => m.CurrentValue == new PermissionsConfig()),
            Mock.Of<IOptionsMonitor<SandboxConfig>>(m => m.CurrentValue == new SandboxConfig()),
            NullLogger<ToolInvocationGovernor>.Instance);

        var pipeline = AdmissionHarness.Pipeline(governor: governor, trace: trace);

        return await pipeline.AdmitAsync(
            new ToolCallAdmissionRequest(SinkTool, CompositionTaint: composition), CancellationToken.None);
    }

    private void AssertApprovalWasSought(string expectedReasonFragment) =>
        _approvalRouter.Verify(
            x => x.RequestApprovalAsync(
                Agent,
                It.IsAny<string>(),
                It.Is<string>(reason => reason.Contains(expectedReasonFragment, StringComparison.Ordinal)),
                It.IsAny<BlastRadius>(),
                It.IsAny<IReadOnlyDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

    private void AssertNoApprovalWasSought() =>
        _approvalRouter.Verify(
            x => x.RequestApprovalAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
}

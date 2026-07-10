using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Bundles;
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
/// Verifies that a bundle run forces the governor on. Enforcement derives from the single ambient fact that
/// a <see cref="CapabilityEnvelope"/> has been published for the flow — so with global
/// <c>EnforceToolInvocation</c> left off (its default), an active envelope still makes the governor evaluate
/// every tool call, and a missing agent identity fails closed. Outside a bundle run the governor stays a pure
/// pass-through — the existing default behavior.
/// </summary>
public sealed class ToolInvocationGovernorEnvelopeTests
{
    private const string Agent = "bundle-agent";
    private const string Tool = "file_system";

    private readonly Mock<IAgentExecutionContext> _context = new();
    private readonly Mock<IToolPermissionService> _permissions = new();
    private readonly Mock<IAutonomyDecisionEvaluator> _autonomy = new();
    private readonly Mock<IGovernancePolicyEngine> _policyEngine = new();
    private readonly Mock<IDenialTracker> _denialTracker = new();
    private readonly Mock<ICapabilityEnforcer> _capabilities = new();
    private readonly IToolRiskClassifier _riskClassifier =
        Mock.Of<IToolRiskClassifier>(c => c.Classify(It.IsAny<string>()) == new ToolRiskProfile(BlastRadius.Low, true));

    // Global enforcement OFF — the host has not opted in. Only an active bundle envelope should flip it on.
    private readonly GovernanceConfig _governanceOff = new() { EnforceToolInvocation = false };
    private readonly PermissionsConfig _permissionsConfig = new();
    private readonly SandboxConfig _sandbox = new();

    public ToolInvocationGovernorEnvelopeTests()
    {
        _context.Setup(x => x.AgentId).Returns(Agent);
        _permissions
            .Setup(x => x.ResolvePermissionAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionDecision.Deny("outside the envelope"));
        _capabilities
            .Setup(x => x.EnforceAsync(It.IsAny<string>(), It.IsAny<Domain.AI.Sandbox.ToolCapability>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _policyEngine.SetupGet(x => x.HasPolicies).Returns(false);
    }

    private ToolInvocationGovernor Build() => new(
        _context.Object, _permissions.Object, _riskClassifier, _autonomy.Object, _policyEngine.Object,
        Mock.Of<IGovernanceAuditService>(), _denialTracker.Object, _capabilities.Object,
        Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == _governanceOff),
        Mock.Of<IOptionsMonitor<PermissionsConfig>>(m => m.CurrentValue == _permissionsConfig),
        Mock.Of<IOptionsMonitor<SandboxConfig>>(m => m.CurrentValue == _sandbox),
        NullLogger<ToolInvocationGovernor>.Instance);

    private static CapabilityEnvelope Envelope() => new() { AllowedTools = [Tool] };

    [Fact]
    public async Task GlobalOff_NoBundleRun_PassesThroughWithoutEvaluating()
    {
        var governor = Build();

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.True(decision.IsAllowed);
        _permissions.Verify(x => x.ResolvePermissionAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GlobalOff_InsideBundleRun_EnforcesAndBlocksOutOfEnvelopeTool()
    {
        var governor = Build();

        ToolInvocationDecision decision;
        using (CapabilityEnvelopeAccessor.Begin(Envelope()))
            decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        _permissions.Verify(x => x.ResolvePermissionAsync(Agent, Tool,
            It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GlobalOff_InsideBundleRun_MissingAgentId_FailsClosed()
    {
        // An ephemeral agent whose identity is missing is exactly the shape a governance bypass would take.
        // Off the bundle path this passes through ungoverned; inside a bundle run it MUST deny.
        _context.Setup(x => x.AgentId).Returns((string?)null);
        var governor = Build();

        ToolInvocationDecision decision;
        using (CapabilityEnvelopeAccessor.Begin(Envelope()))
            decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        // The permission service is never consulted — we deny before needing an identity.
        _permissions.Verify(x => x.ResolvePermissionAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Trace_AfterEnvelopeScopeDisposed_StillReportsEnforced()
    {
        // The trace may be assembled after the run's ambient envelope has torn down. A turn that authorized
        // under enforcement must still report EnforcementEnabled=true, not be mislabelled as ungoverned.
        var governor = Build();

        using (CapabilityEnvelopeAccessor.Begin(Envelope()))
            await governor.AuthorizeAsync(Tool, CancellationToken.None);

        // Scope disposed — the ambient envelope is gone, but the trace must still say the turn was enforced.
        Assert.True(governor.GetTrace().EnforcementEnabled);
    }

    [Fact]
    public async Task BundleRunEnded_ReturnsToPassThrough()
    {
        var governor = Build();

        using (CapabilityEnvelopeAccessor.Begin(Envelope()))
            await governor.AuthorizeAsync(Tool, CancellationToken.None);

        governor.Reset();
        _permissions.Invocations.Clear();
        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.True(decision.IsAllowed);
        _permissions.Verify(x => x.ResolvePermissionAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

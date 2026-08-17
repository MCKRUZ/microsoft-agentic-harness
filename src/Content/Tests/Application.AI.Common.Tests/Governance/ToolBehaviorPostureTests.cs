using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Tools;
using Domain.AI.Bundles;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.Common;
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
/// The non-read-only approval posture, exercised through the <em>real</em> admission chain rather than
/// against the governor in isolation.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the real chain.</strong> The posture's value is that every execution path inherits it,
/// and every path reaches tools through <see cref="ToolCallAdmissionPipeline"/> and nothing else. A
/// test that called the governor directly would prove the rule exists; driving the chain proves the
/// rule is reachable from where calls actually arrive. The per-path tests that prove each caller uses
/// the chain already exist and are not duplicated here — that property belongs to the chain, not to
/// this rule, and re-asserting it once per gate is how five copies of a sequence appeared in the first
/// place.
/// </para>
/// <para>
/// <strong>Approval routing is left off throughout,</strong> which is the shipped default: an approval
/// verdict with no router degrades to a block. That makes the assertions binary — the call was refused
/// or it was not — rather than depending on a simulated human.
/// </para>
/// </remarks>
public sealed class ToolBehaviorPostureTests
{
    private const string Agent = "test-agent";
    private const string Tool = "notion_create_page";

    private readonly Mock<IAgentExecutionContext> _context = new();
    private readonly Mock<IToolPermissionService> _permissions = new();
    private readonly Mock<IGovernancePolicyEngine> _policyEngine = new();
    private readonly Mock<ICapabilityEnforcer> _capabilities = new();
    private readonly Mock<IToolApprovalRouter> _approvalRouter = new();
    private readonly ToolBehaviorRegistry _behavior;

    private readonly IToolRiskClassifier _riskClassifier =
        Mock.Of<IToolRiskClassifier>(c =>
            c.Classify(It.IsAny<string>()) == new ToolRiskProfile(BlastRadius.Low, true));

    public ToolBehaviorPostureTests()
    {
        _context.Setup(x => x.AgentId).Returns(Agent);
        _permissions
            .Setup(x => x.ResolvePermissionAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionDecision.Allow("allowed by default"));
        _capabilities
            .Setup(x => x.EnforceAsync(It.IsAny<string>(), It.IsAny<Domain.AI.Sandbox.ToolCapability>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _policyEngine.SetupGet(x => x.HasPolicies).Returns(false);
        _approvalRouter
            .Setup(x => x.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolApprovalResult.NotRouted("tool approval routing is disabled"));

        // A real registry, not a mock: half the behaviour under test is which declaration wins, and a
        // mocked registry would let this file assert whatever it liked about that.
        _behavior = new ToolBehaviorRegistry(new ServiceCollection().BuildServiceProvider());
    }

    [Fact]
    public void PostureDefault_IsOff_OnAConfigWithNothingSet()
    {
        // A default is untested unless a test builds the config with nothing set. Every other test here
        // switches the posture on explicitly, so without this one "off by default" would be a claim in
        // a comment.
        var shipped = new GovernanceConfig();

        Assert.False(shipped.ToolBehaviorGating.RequireApprovalForNonReadOnlyTools);
        Assert.Empty(shipped.ToolBehaviorGating.Exemptions);
    }

    [Fact]
    public async Task PostureOff_AToolNobodyHasDescribed_RunsWithoutApproval()
    {
        // The control for every refusal below. Without it, a gate that refused everything
        // unconditionally would pass the whole file.
        var admission = await Admit(PostureOff);

        Assert.True(admission.IsAllowed);
    }

    [Fact]
    public async Task PostureOn_AToolNobodyHasDescribed_IsGated()
    {
        var admission = await Admit(PostureOn);

        Assert.False(admission.IsAllowed);
        AssertApprovalWasSought("nothing is known about what the tool does");
    }

    [Fact]
    public async Task PostureOn_AToolDeclaredReadOnlyByATrustedServer_RunsWithoutApproval()
    {
        // The pass case, and the reason it is not enough to assert refusals: this is what proves the
        // gate can tell tools apart rather than simply refusing everything while the posture is on.
        _behavior.RecordAdvertised(Tool, Advertised("notion", ReadOnly: true, trusted: true));

        var admission = await Admit(PostureOn);

        Assert.True(admission.IsAllowed);
        AssertNoApprovalWasSought();
    }

    [Fact]
    public async Task PostureOn_AToolDeclaredReadOnlyByAnUntrustedServer_IsStillGated()
    {
        // The security property the reference implementation does not have. A server that wants past
        // the gate marks its tool read-only; being unvouched-for is what stops that working.
        _behavior.RecordAdvertised(Tool, Advertised("marketplace-server", ReadOnly: true));

        var admission = await Admit(PostureOn);

        Assert.False(admission.IsAllowed);
        AssertApprovalWasSought("not marked as trusted");
    }

    [Fact]
    public async Task PostureOn_AToolThatAppearsFromAServerAfterStartup_IsGatedWithNoListEdit()
    {
        // The whole point of gating on behaviour. Nothing about this tool exists in any configuration:
        // it is advertised mid-run, recorded as it is discovered, and gated on what it declared.
        var pipeline = Pipeline(PostureOn);

        _behavior.RecordAdvertised("newly_appeared_tool", Advertised("marketplace-server", Destructive: true));

        var admission = await pipeline.AdmitAsync(
            new ToolCallAdmissionRequest("newly_appeared_tool"), CancellationToken.None);

        Assert.False(admission.IsAllowed);
        AssertApprovalWasSought("declares itself destructive");
    }

    [Fact]
    public async Task PostureOn_AnExemptedToolFromTheNamedServer_RunsWithoutApproval()
    {
        // The escape hatch for a declaration that is wrong in the direction that costs an approval on
        // every call — a search endpoint that posts, and is therefore assumed to write.
        _behavior.RecordAdvertised(Tool, Advertised("notion", ReadOnly: false));

        var admission = await Admit(Exempting(new ToolBehaviorExemption
        {
            Tool = Tool,
            Server = "notion",
            Reason = "search endpoint that uses POST and does not mutate; verified against the vendor's API docs",
        }));

        Assert.True(admission.IsAllowed);
        AssertNoApprovalWasSought();
    }

    [Fact]
    public async Task PostureOn_AnExemptionNamingNoServer_DoesNotCoverAnUnvouchedForServersTool()
    {
        // The hole this check closes. A tool name belongs to nobody: the operator exempts the name
        // after checking one vendor's tool, and any other configured server can advertise a tool by
        // that name tomorrow. The registry already refuses to let a shadowing server loosen a record it
        // did not create — a bare-name exemption applied on top would hand that bypass straight back.
        _behavior.RecordAdvertised(Tool, Advertised("some-other-server", Destructive: true));

        var admission = await Admit(Exempting(new ToolBehaviorExemption
        {
            Tool = Tool,
            Reason = "checked the vendor's version of this tool; it only reads",
        }));

        Assert.False(admission.IsAllowed);
        AssertApprovalWasSought("declares itself destructive");
    }

    [Fact]
    public async Task PostureOn_AnExemptionNamingADifferentServer_DoesNotCoverThisOne()
    {
        // The mutation control for the pass case above: naming a server must mean matching it, not
        // merely supplying one.
        _behavior.RecordAdvertised(Tool, Advertised("shadowing-server", ReadOnly: false));

        var admission = await Admit(Exempting(new ToolBehaviorExemption
        {
            Tool = Tool,
            Server = "notion",
            Reason = "verified against the vendor's API docs",
        }));

        Assert.False(admission.IsAllowed);
    }

    [Fact]
    public async Task PostureOn_AnExemptionNamingNoServer_StillCoversAVouchedForTool()
    {
        // The server name is required only where it carries weight. For a tool from a server the
        // operator already trusts, demanding it a second time would be ceremony.
        _behavior.RecordAdvertised(Tool, Advertised("notion", ReadOnly: false, trusted: true));

        var admission = await Admit(Exempting(new ToolBehaviorExemption
        {
            Tool = Tool,
            Reason = "search endpoint that uses POST and does not mutate",
        }));

        Assert.True(admission.IsAllowed);
        AssertNoApprovalWasSought();
    }

    [Fact]
    public async Task PostureOn_AnExemptionForADifferentTool_DoesNotCoverThisOne()
    {
        // Mutation control for the test above: the exemption must be matched, not merely present.
        var admission = await Admit(
            Exempting(new ToolBehaviorExemption { Tool = "some_other_tool", Reason = "unrelated" }));

        Assert.False(admission.IsAllowed);
    }

    [Fact]
    public async Task PostureOn_EnforcementOff_OutsideABundleRun_ChangesNothing()
    {
        // Half of the reason GovernanceConfigValidator refuses this combination outright. Neither this
        // test nor the next endorses it — together they pin what the code actually does, so the
        // validator's message stays true if the rule is ever relaxed.
        var admission = await Admit(EnforcementOffPostureOn);

        Assert.True(admission.IsAllowed);
        AssertNoApprovalWasSought();
    }

    [Fact]
    public async Task PostureOn_EnforcementOff_InsideABundleRun_StillGates()
    {
        // The other half, and the uncomfortable one. The governor arms on EITHER invocation
        // enforcement OR an ambient capability envelope, so leaving enforcement off does not switch the
        // posture off — it applies it to bundle runs alone while every agent turn and plan step goes
        // ungated. An earlier version of this file asserted the opposite in a comment, in a test that
        // armed no envelope and therefore could not have caught it.
        using var _ = CapabilityEnvelopeAccessor.Begin(new CapabilityEnvelope { AllowedTools = [Tool] });

        var admission = await Admit(EnforcementOffPostureOn);

        Assert.False(admission.IsAllowed);
        AssertApprovalWasSought("nothing is known about what the tool does");
    }

    /// <summary>Invocation enforcement on — the only condition under which the posture does anything.</summary>
    private static GovernanceConfig Enforcing(ToolBehaviorGatingConfig? gating = null) => new()
    {
        EnforceToolInvocation = true,
        EnableAudit = true,
        ToolBehaviorGating = gating ?? new ToolBehaviorGatingConfig(),
    };

    private static GovernanceConfig PostureOff => Enforcing();

    private static GovernanceConfig PostureOn =>
        Enforcing(new ToolBehaviorGatingConfig { RequireApprovalForNonReadOnlyTools = true });

    private static GovernanceConfig EnforcementOffPostureOn => new()
    {
        EnforceToolInvocation = false,
        ToolBehaviorGating = new ToolBehaviorGatingConfig { RequireApprovalForNonReadOnlyTools = true },
    };

    /// <summary>The posture on, with one exemption in force.</summary>
    private static ToolBehaviorGatingConfig Exempting(ToolBehaviorExemption exemption) =>
        new() { RequireApprovalForNonReadOnlyTools = true, Exemptions = [exemption] };

    /// <summary>A declaration as an MCP server would make it, attributed to that server.</summary>
    private static ToolBehavior Advertised(
        string serverName, bool? ReadOnly = null, bool? Destructive = null, bool trusted = false) =>
        new(trusted ? ToolBehaviorSource.TrustedMcpServer : ToolBehaviorSource.UntrustedMcpServer,
            ReadOnly: ReadOnly,
            Destructive: Destructive,
            ServerName: serverName);

    private Task<ToolCallAdmission> Admit(ToolBehaviorGatingConfig gating) => Admit(Enforcing(gating));

    private Task<ToolCallAdmission> Admit(GovernanceConfig governance) =>
        Pipeline(governance).AdmitAsync(new ToolCallAdmissionRequest(Tool), CancellationToken.None).AsTask();

    /// <summary>
    /// Builds the real admission chain around a real governor, so a call arrives the way one does at
    /// runtime rather than through a direct call to the rule under test.
    /// </summary>
    private ToolCallAdmissionPipeline Pipeline(GovernanceConfig governance)
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

        return AdmissionHarness.Pipeline(governor: governor, trace: trace);
    }

    /// <summary>
    /// Asserts a human was asked, and that the question named the right reason — a refusal alone would
    /// also be produced by a gate that blocks for some unrelated cause.
    /// </summary>
    private void AssertApprovalWasSought(string expectedReasonFragment) =>
        _approvalRouter.Verify(
            x => x.RequestApprovalAsync(
                Agent,
                It.IsAny<string>(),
                It.Is<string>(reason => reason.Contains(expectedReasonFragment, StringComparison.Ordinal)),
                It.IsAny<BlastRadius>(),
                It.IsAny<IReadOnlyDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

    private void AssertNoApprovalWasSought() =>
        _approvalRouter.Verify(
            x => x.RequestApprovalAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
}

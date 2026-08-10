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

    // Approval routing off, which is the shipped default and the behaviour every test in this class
    // predates: an approval verdict blocks without asking anyone. Tests that exercise routing set
    // their own expectation on this mock.
    private readonly Mock<IToolApprovalRouter> _approvalRouter = new();

    private readonly IToolRiskClassifier _riskClassifier =
        Mock.Of<IToolRiskClassifier>(c => c.Classify(It.IsAny<string>()) == new ToolRiskProfile(BlastRadius.Low, true));

    /// <summary>
    /// What the tool under test has declared about itself. Defaults to <see cref="ToolBehavior.Unknown"/>
    /// — the fail-closed answer — so a test that forgets to arrange a declaration exercises the gated
    /// case rather than the exempt one.
    /// </summary>
    private readonly Mock<IToolBehaviorRegistry> _behavior = new();

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
        _behavior.Setup(x => x.Resolve(It.IsAny<string>())).Returns(ToolBehavior.Unknown);
        _approvalRouter
            .Setup(x => x.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolApprovalResult.NotRouted("tool approval routing is disabled"));
    }

    /// <summary>
    /// The turn's governance trail, which the governor writes to and this fixture reads. Assigned by
    /// <see cref="Build"/>, because it reads the governor's config: whether a turn counts as governed
    /// is derived from the same switch, so a recorder built from a different config would disagree with
    /// the governor it is recording for.
    /// </summary>
    private GovernanceTraceRecorder _trace = null!;

    /// <summary>The trail as the turn handler reads it. Real, not mocked — it is the assertion target.</summary>
    private GovernanceTrace Trace => _trace.Snapshot();

    /// <summary>
    /// Builds the governor under test. Pass <paramref name="governance"/> to override the default
    /// config — the only thing the per-test constructions ever varied, which is why they were folded
    /// back into this helper: each was an 8-line copy that had to be edited whenever the constructor
    /// gained a parameter.
    /// </summary>
    private ToolInvocationGovernor Build(GovernanceConfig? governance = null)
    {
        var governanceMonitor =
            Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == (governance ?? _governance));
        _trace = new GovernanceTraceRecorder(governanceMonitor, _riskClassifier);

        return new ToolInvocationGovernor(
            _context.Object,
            _permissions.Object,
            _riskClassifier,
            _behavior.Object,
            _autonomy.Object,
            _policyEngine.Object,
            Mock.Of<IGovernanceAuditService>(),
            _denialTracker.Object,
            _capabilities.Object,
            _approvalRouter.Object,
            _trace,
            governanceMonitor,
            Mock.Of<IOptionsMonitor<PermissionsConfig>>(m => m.CurrentValue == _permissionsConfig),
            Mock.Of<IOptionsMonitor<SandboxConfig>>(m => m.CurrentValue == _sandbox),
            NullLogger<ToolInvocationGovernor>.Instance);
    }

    [Fact]
    public async Task AuthorizeAsync_EnforcementDisabled_AllowsAndDoesNotEvaluate()
    {
        var governance = new GovernanceConfig { EnforceToolInvocation = false };
        var governor = Build(governance);

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.True(decision.IsAllowed);
        Assert.Same(GovernanceTrace.Empty, Trace);
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
        var trace = Trace;
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
        var record = Assert.Single(Trace.ToolDecisions);
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
        var trace = Trace;
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
        var record = Assert.Single(Trace.ToolDecisions);
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
        var record = Assert.Single(Trace.ToolDecisions);
        Assert.Equal(ToolDecisionOutcome.Denied, record.Outcome);
        Assert.Contains("capability", record.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reset_ClearsPriorTurnDecisions_NoCrossTurnDoubleCount()
    {
        // The trail is scoped but shared across turns of a conversation (nested MediatR sends share
        // one DI scope), so each turn must reset it or the trace accumulates and the merged
        // conversation trace double-counts. This guards that regression.
        var governor = Build();

        // Turn 1
        await governor.AuthorizeAsync(Tool, CancellationToken.None);
        Assert.Equal(1, Trace.ToolInvocationCount);

        // Turn 2 begins. The reset is on the trail now, not the governor — the governor keeps nothing
        // to clear.
        _trace.Reset();
        await governor.AuthorizeAsync(Tool, CancellationToken.None);

        var trace = Trace;
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
            .Setup(x => x.EvaluateToolCall(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns(GovernanceDecision.Denied("rule-7", "default-policy", "blocked by policy"));

        var governor = Build(governance);

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(ToolDecisionOutcome.Denied, Assert.Single(Trace.ToolDecisions).Outcome);
    }

    [Fact]
    public async Task AuthorizeAsync_ForwardsCallArgumentsToThePolicyEngine()
    {
        // The policy engine builds its rule-evaluation context out of these, so a rule conditioned on
        // an argument value ("deny sql_query where database == 'prod'") can only ever match when they
        // are supplied. Passing the tool name alone did not make such a rule deny-by-default — it made
        // it unmatchable, so an operator's rule loaded, reported as active, and silently never fired.
        IReadOnlyDictionary<string, object?>? seen = null;
        var governance = new GovernanceConfig { EnforceToolInvocation = true, Enabled = true };
        _policyEngine.SetupGet(x => x.HasPolicies).Returns(true);
        _policyEngine
            .Setup(x => x.EvaluateToolCall(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Callback<string, string, IReadOnlyDictionary<string, object?>?>((_, _, args) => seen = args)
            .Returns(GovernanceDecision.Allowed());

        var governor = Build(governance);

        var arguments = new Dictionary<string, object?> { ["database"] = "prod" };
        await governor.AuthorizeAsync(Tool, CancellationToken.None, arguments);

        Assert.NotNull(seen);
        Assert.Equal("prod", seen!["database"]);
    }

    private void RouterAnswers(ToolApprovalResult result) =>
        _approvalRouter
            .Setup(x => x.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private void AskingPermission() =>
        _permissions
            .Setup(x => x.ResolvePermissionAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionDecision.Ask("needs human sign-off"));

    [Fact]
    public async Task AuthorizeAsync_ApprovalRequiredAndHumanApproves_LetsTheCallThrough()
    {
        // The behaviour this whole feature exists for: an approval verdict used to be a dead end.
        AskingPermission();
        RouterAnswers(ToolApprovalResult.Approved("approved by alice", Guid.NewGuid()));
        var governor = Build();

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.True(decision.IsAllowed);
        var record = Assert.Single(Trace.ToolDecisions);
        Assert.Equal(ToolDecisionOutcome.Allowed, record.Outcome);
        Assert.True(record.RequiredApproval);
        Assert.True(record.ApprovalGranted);
        _denialTracker.Verify(x => x.RecordDenial(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task AuthorizeAsync_HumanApproves_ButCapabilityEnforcementStillRefuses_Blocks()
    {
        // A human answers the PERMISSION question. They do not answer the capability question, and
        // must not be able to. This regressed once: an approved Ask returned Allow directly, so an
        // approver clicking "yes" could run a tool with a sandbox capability the host never granted.
        AskingPermission();
        RouterAnswers(ToolApprovalResult.Approved("approved by alice", Guid.NewGuid()));
        _capabilities
            .Setup(x => x.EnforceAsync(It.IsAny<string>(), It.IsAny<Domain.AI.Sandbox.ToolCapability>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Fail("tool requires a capability the sandbox did not grant"));
        var governor = Build();

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(ToolDecisionOutcome.Denied, Assert.Single(Trace.ToolDecisions).Outcome);
    }

    [Fact]
    public async Task AuthorizeAsync_HumanApproves_ButEnvelopeDoesNotGrantTheTool_Blocks()
    {
        // Same invariant against the capability envelope, whose independent re-check exists because
        // the permission resolver's arbitration has been wrong twice before.
        AskingPermission();
        RouterAnswers(ToolApprovalResult.Approved("approved by alice", Guid.NewGuid()));
        using var envelope = CapabilityEnvelopeAccessor.Begin(
            new Domain.AI.Bundles.CapabilityEnvelope { AllowedTools = ["something_else"] });
        var governor = Build();

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(ToolDecisionOutcome.Denied, Assert.Single(Trace.ToolDecisions).Outcome);
    }

    [Fact]
    public async Task AuthorizeAsync_DeterministicRefusal_NeverPagesAHuman()
    {
        // Approvers must not be asked to rule on a call another gate was always going to refuse.
        // Beyond wasting their attention, it stalls the agent's turn for the whole approval timeout
        // to reach a denial that was knowable up front — and teaches approvers their answer is moot.
        AskingPermission();
        _capabilities
            .Setup(x => x.EnforceAsync(It.IsAny<string>(), It.IsAny<Domain.AI.Sandbox.ToolCapability>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Fail("tool requires a capability the sandbox did not grant"));
        var governor = Build();

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        _approvalRouter.Verify(x => x.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AuthorizeAsync_TwoGatesWantAHuman_AsksOnceShowingBothReasons()
    {
        // The permission layer and the policy engine demand approval for DIFFERENT reasons, written
        // by different authors. Showing only the first would mean a human approving "needs sign-off"
        // silently clears "production schema changes need DBA review" — a question never put to them.
        var governance = new GovernanceConfig { EnforceToolInvocation = true, Enabled = true, EnableAudit = true };
        AskingPermission();
        _policyEngine.SetupGet(x => x.HasPolicies).Returns(true);
        _policyEngine
            .Setup(x => x.EvaluateToolCall(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns(new GovernanceDecision(
                IsAllowed: false,
                Action: GovernancePolicyAction.RequireApproval,
                Reason: "production schema changes need DBA review",
                MatchedRule: "rule-9",
                PolicyName: "default-policy"));

        string? askedReason = null;
        _approvalRouter
            .Setup(x => x.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, BlastRadius, IReadOnlyDictionary<string, object?>?, CancellationToken>(
                (_, _, reason, _, _, _) => askedReason = reason)
            .ReturnsAsync(ToolApprovalResult.Approved("approved by alice", Guid.NewGuid()));

        var governor = Build(governance);

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.True(decision.IsAllowed);
        _approvalRouter.Verify(x => x.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.NotNull(askedReason);
        Assert.Contains("needs human sign-off", askedReason, StringComparison.Ordinal);
        Assert.Contains("DBA review", askedReason, StringComparison.Ordinal);
    }

    // RecordDownstreamBlock's ungoverned-turn and enforced-before-any-authorize branches are pure
    // GovernanceTraceRecorder behavior now — no governor involved — and are covered directly in
    // GovernanceTraceRecorderTests. What stays here is the one case that genuinely needs a real
    // governor: proving a governor's own Allowed record and a downstream Denied record coexist.
    [Fact]
    public async Task RecordDownstreamBlock_AfterAnAllow_MakesTheTraceTellTheTruth()
    {
        // A gate running after the governor (the classification gate, the progress guard, a consumer
        // observer) can stop a call the governor allowed. Without this the trace would report the
        // call as Allowed, and every consumer of it would be wrong for exactly the calls a safety
        // rule stopped. Both records are kept: the governor did allow it, something downstream did
        // not, and a trail showing only one of those is telling half the story.
        var governor = Build();
        await governor.AuthorizeAsync(Tool, CancellationToken.None);

        _trace.RecordDownstreamBlock(Tool, "blocked by observer 'wire-limit'");

        var decisions = Trace.ToolDecisions;
        Assert.Contains(decisions, d => d.Outcome == ToolDecisionOutcome.Allowed);
        Assert.Contains(decisions, d => d.Outcome == ToolDecisionOutcome.Denied);
    }

    [Fact]
    public async Task AuthorizeAsync_ApprovalRequiredAndHumanRefuses_StillBlocks()
    {
        AskingPermission();
        RouterAnswers(ToolApprovalResult.Denied("an approver refused the call", Guid.NewGuid()));
        var governor = Build();

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        var record = Assert.Single(Trace.ToolDecisions);
        Assert.Equal(ToolDecisionOutcome.PendingApproval, record.Outcome);
        Assert.False(record.ApprovalGranted);
        _denialTracker.Verify(x => x.RecordDenial(Agent, Tool, null), Times.Once);
    }

    [Fact]
    public async Task AuthorizeAsync_ApprovalVerdict_PassesTheCallArgumentsToTheApprover()
    {
        // Without the arguments an approver is being asked to sign off on a tool name alone.
        AskingPermission();
        IReadOnlyDictionary<string, object?>? seen = null;
        _approvalRouter
            .Setup(x => x.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, BlastRadius, IReadOnlyDictionary<string, object?>?, CancellationToken>(
                (_, _, _, _, args, _) => seen = args)
            .ReturnsAsync(ToolApprovalResult.Denied("refused"));
        var governor = Build();

        var arguments = new Dictionary<string, object?> { ["path"] = "/etc/passwd" };
        await governor.AuthorizeAsync(Tool, CancellationToken.None, arguments);

        Assert.NotNull(seen);
        Assert.Equal("/etc/passwd", seen["path"]);
    }

    [Fact]
    public async Task AuthorizeAsync_PolicyRequiresApprovalAndHumanApproves_LetsTheCallThrough()
    {
        // The second approval source. Both converge on one routing path so they cannot drift.
        var governance = new GovernanceConfig { EnforceToolInvocation = true, Enabled = true, EnableAudit = true };
        _policyEngine.SetupGet(x => x.HasPolicies).Returns(true);
        _policyEngine
            .Setup(x => x.EvaluateToolCall(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns(new GovernanceDecision(
                IsAllowed: false,
                Action: GovernancePolicyAction.RequireApproval,
                Reason: "high blast radius",
                MatchedRule: "rule-9",
                PolicyName: "default-policy"));
        RouterAnswers(ToolApprovalResult.Approved("approved by alice", Guid.NewGuid()));

        var governor = Build(governance);

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.True(decision.IsAllowed);
        Assert.True(Assert.Single(Trace.ToolDecisions).ApprovalGranted);
    }

    [Fact]
    public async Task AuthorizeAsync_ApprovalRoutingNotConfigured_BehavesExactlyAsBeforeTheFeature()
    {
        // The regression guard for every existing deployment: routing off must be byte-identical to
        // the old dead-end block, not merely "also a block".
        AskingPermission();
        RouterAnswers(ToolApprovalResult.NotRouted("tool approval routing is disabled"));
        var governor = Build();

        var decision = await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        var record = Assert.Single(Trace.ToolDecisions);
        Assert.Equal(ToolDecisionOutcome.PendingApproval, record.Outcome);
        Assert.True(record.RequiredApproval);
        Assert.False(record.ApprovalGranted);
        _denialTracker.Verify(x => x.RecordDenial(Agent, Tool, null), Times.Once);
    }

    [Theory]
    [InlineData("255")]                     // every bit, including undefined ones
    [InlineData(" 255")]                    // and behind a stray space
    [InlineData("4")]                       // the numeric form of NetworkAccess
    public async Task AuthorizeAsync_NumericGrantedCapability_IsNotGrantedToTheEnforcer(string entry)
    {
        // #300. DefaultGrantedCapabilities is a GRANT list on the live tool path, so a permissive
        // parse fails open. ToolCapability is [Flags], and Enum.TryParse accepts "255" and sets
        // every bit — handing the enforcer every capability the sandbox model defines and making the
        // check below it unfailable. The assertion is on what the enforcer was actually handed,
        // because that is the value the check consumes.
        Domain.AI.Sandbox.ToolCapability granted = default;
        _capabilities
            .Setup(x => x.EnforceAsync(It.IsAny<string>(), It.IsAny<Domain.AI.Sandbox.ToolCapability>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Domain.AI.Sandbox.ToolCapability, IReadOnlyList<string>?, IReadOnlyList<string>?, CancellationToken>(
                (_, caps, _, _, _) => granted = caps)
            .ReturnsAsync(Result.Success());

        _sandbox.DefaultGrantedCapabilities.Clear();
        _sandbox.DefaultGrantedCapabilities.Add("FileRead");
        _sandbox.DefaultGrantedCapabilities.Add(entry);

        var governor = Build();

        await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.Equal(Domain.AI.Sandbox.ToolCapability.FileRead, granted);
    }

    [Fact]
    public async Task AuthorizeAsync_NamedGrantedCapabilities_AreStillGranted()
    {
        // The control for the theory above: refusing non-names must not mean granting nothing.
        // Combinations stay expressible — as separate entries, which is the shape the config uses.
        Domain.AI.Sandbox.ToolCapability granted = default;
        _capabilities
            .Setup(x => x.EnforceAsync(It.IsAny<string>(), It.IsAny<Domain.AI.Sandbox.ToolCapability>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Domain.AI.Sandbox.ToolCapability, IReadOnlyList<string>?, IReadOnlyList<string>?, CancellationToken>(
                (_, caps, _, _, _) => granted = caps)
            .ReturnsAsync(Result.Success());

        _sandbox.DefaultGrantedCapabilities.Clear();
        _sandbox.DefaultGrantedCapabilities.Add("FileRead");
        _sandbox.DefaultGrantedCapabilities.Add("Subprocess");

        var governor = Build();

        await governor.AuthorizeAsync(Tool, CancellationToken.None);

        Assert.Equal(
            Domain.AI.Sandbox.ToolCapability.FileRead | Domain.AI.Sandbox.ToolCapability.Subprocess,
            granted);
    }
}

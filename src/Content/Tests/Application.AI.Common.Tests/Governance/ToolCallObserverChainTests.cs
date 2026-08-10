using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Changes;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Verifies the consumer-authored observer seam: host rules are consulted on every tool call, the
/// strictest ruling wins, and every abnormal exit blocks rather than proceeds.
/// </summary>
/// <remarks>
/// The seam's whole safety argument is that an observer can only tighten an outcome. These tests pin
/// that — most importantly that an observer which crashes stops the call, because a consumer's
/// safety rule that silently stops applying the moment it has a bug is worse than no rule at all.
/// </remarks>
public sealed class ToolCallObserverChainTests
{
    private const string Agent = "test-agent";
    private const string Tool = "wire_funds";

    private readonly Mock<IToolApprovalRouter> _approvalRouter = new();
    private readonly Mock<IAgentExecutionContext> _context = new();

    public ToolCallObserverChainTests()
    {
        _context.Setup(x => x.AgentId).Returns(Agent);
        _context.Setup(x => x.ConversationId).Returns("conv-1");
        _context.Setup(x => x.TurnNumber).Returns(1);
        _approvalRouter
            .Setup(x => x.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolApprovalResult.NotRouted("routing disabled"));
    }

    private readonly Mock<IGovernanceTraceRecorder> _trace = new();

    private ToolCallObserverChain Build(params IToolCallObserver[] observers) => new(
        observers,
        _approvalRouter.Object,
        Mock.Of<IToolRiskClassifier>(c => c.Classify(It.IsAny<string>()) == new ToolRiskProfile(BlastRadius.High, false)),
        _context.Object,
        Mock.Of<IGovernanceAuditService>(),
        _trace.Object,
        Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == new GovernanceConfig()),
        NullLogger<ToolCallObserverChain>.Instance);

    [Fact]
    public async Task EvaluateAsync_BlockedCall_CorrectsTheGovernorsTrace()
    {
        // The governor recorded this call as Allowed — truthfully, that was its own verdict — and the
        // chain runs after it. Without this correction the trace reports Allowed for a call that never
        // executed, and every consumer of it (bundle reporting, the dashboard, the audit) is wrong for
        // precisely the calls a consumer's safety rule stopped.
        //
        // The trace recorder is a constructor dependency here. It used to be reached through the
        // governor, which meant this correction silently did not happen on any path that had not
        // armed a governor to reach it through.
        var chain = Build(new StubObserver("wire-limit", ToolCallVerdict.Block("over the limit")));

        await Evaluate(chain);

        _trace.Verify(
            t => t.RecordDownstreamBlock(Tool, It.Is<string>(r => r.Contains("wire-limit"))),
            Times.Once);
    }

    private static ValueTask<ToolInvocationDecision> Evaluate(ToolCallObserverChain chain) =>
        chain.EvaluateAsync(Tool, new Dictionary<string, object?> { ["amount"] = 50_000 }, CancellationToken.None);

    [Fact]
    public void HasObservers_NoneRegistered_IsFalseSoTheChokepointCanSkipEntirely()
    {
        Assert.False(Build().HasObservers);
    }

    [Fact]
    public async Task EvaluateAsync_NoObservers_Allows()
    {
        var decision = await Evaluate(Build());

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task EvaluateAsync_ObserverProceeds_Allows()
    {
        var decision = await Evaluate(Build(new StubObserver("noop", ToolCallVerdict.Proceed())));

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task EvaluateAsync_ObserverBlocks_StopsTheCall()
    {
        // The headline case: a consumer's domain rule stopping a specific invocation.
        var decision = await Evaluate(Build(
            new StubObserver("wire-limit", ToolCallVerdict.Block("amount exceeds 10,000"))));

        Assert.False(decision.IsAllowed);
        Assert.NotNull(decision.DeniedMessage);
    }

    [Fact]
    public async Task EvaluateAsync_BlockedCall_TellsTheModelNothingAboutWhichRuleFired()
    {
        // The reason is operator-facing only. Leaking it would let a model map the rule set by
        // probing, which is exactly how an adversarial turn finds the edges of a policy.
        var decision = await Evaluate(Build(
            new StubObserver("wire-limit", ToolCallVerdict.Block("amount exceeds 10,000"))));

        Assert.DoesNotContain("10,000", decision.DeniedMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("wire-limit", decision.DeniedMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_ObserverThrows_BlocksRatherThanAbstains()
    {
        // A rule that cannot run has not cleared the action it was there to judge.
        var decision = await Evaluate(Build(new ThrowingObserver("broken")));

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task EvaluateAsync_FirstObjectionWins_LaterObserversNotConsulted()
    {
        var later = new StubObserver("later", ToolCallVerdict.Proceed());
        var decision = await Evaluate(Build(
            new StubObserver("first", ToolCallVerdict.Block("no")),
            later));

        Assert.False(decision.IsAllowed);
        Assert.Equal(0, later.Calls);
    }

    [Fact]
    public async Task EvaluateAsync_APermissiveObserverCannotOverrideARestrictiveOne()
    {
        // Ordering cannot be used to widen access: "proceed" is the most permissive thing any
        // observer can say, so a later block always wins over an earlier proceed.
        var decision = await Evaluate(Build(
            new StubObserver("permissive", ToolCallVerdict.Proceed()),
            new StubObserver("restrictive", ToolCallVerdict.Block("no"))));

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task EvaluateAsync_ObserverEscalatesAndHumanApproves_CallProceeds()
    {
        _approvalRouter
            .Setup(x => x.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolApprovalResult.Approved("approved by alice", Guid.NewGuid()));

        var decision = await Evaluate(Build(
            new StubObserver("wire-limit", ToolCallVerdict.RequireApproval("over the auto-approve limit"))));

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task EvaluateAsync_ApprovedEscalation_StillConsultsRemainingObservers()
    {
        // A human answered the observer that asked; they did not speak for the ones that had not run.
        _approvalRouter
            .Setup(x => x.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolApprovalResult.Approved("approved by alice", Guid.NewGuid()));

        var later = new StubObserver("sanctions-check", ToolCallVerdict.Block("counterparty is sanctioned"));
        var decision = await Evaluate(Build(
            new StubObserver("wire-limit", ToolCallVerdict.RequireApproval("over the limit")),
            later));

        Assert.False(decision.IsAllowed);
        Assert.Equal(1, later.Calls);
    }

    [Fact]
    public async Task EvaluateAsync_ObserverEscalatesButNoApprovalRouteConfigured_Blocks()
    {
        // The observer asked for a human and there is none. Refusing is the only safe answer —
        // proceeding would silently discard the rule.
        var decision = await Evaluate(Build(
            new StubObserver("wire-limit", ToolCallVerdict.RequireApproval("over the limit"))));

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task EvaluateAsync_ObserverEscalatesWithNoAgentIdentity_BlocksWithoutRaisingAnEscalation()
    {
        _context.Setup(x => x.AgentId).Returns((string?)null);

        var decision = await Evaluate(Build(
            new StubObserver("wire-limit", ToolCallVerdict.RequireApproval("over the limit"))));

        Assert.False(decision.IsAllowed);
        _approvalRouter.Verify(x => x.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EvaluateAsync_PassesTheCallAndTurnIdentityToTheObserver()
    {
        var observer = new StubObserver("recorder", ToolCallVerdict.Proceed());

        await Evaluate(Build(observer));

        Assert.NotNull(observer.LastObservation);
        Assert.Equal(Tool, observer.LastObservation.ToolName);
        Assert.Equal(Agent, observer.LastObservation.AgentId);
        Assert.Equal("conv-1", observer.LastObservation.ConversationId);
        Assert.Equal(1, observer.LastObservation.TurnNumber);
        Assert.Equal(50_000, observer.LastObservation.Arguments["amount"]);
    }

    [Fact]
    public async Task EvaluateAsync_TurnCancelled_PropagatesRatherThanBecomingAPolicyVerdict()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var chain = Build(new CancellingObserver());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await chain.EvaluateAsync(Tool, new Dictionary<string, object?>(), cts.Token));
    }

    private sealed class StubObserver(string name, ToolCallVerdict verdict) : IToolCallObserver
    {
        public string Name => name;
        public int Calls { get; private set; }
        public ToolCallObservation? LastObservation { get; private set; }

        public ValueTask<ToolCallVerdict> ObserveAsync(
            ToolCallObservation observation, CancellationToken cancellationToken)
        {
            Calls++;
            LastObservation = observation;
            return ValueTask.FromResult(verdict);
        }
    }

    private sealed class ThrowingObserver(string name) : IToolCallObserver
    {
        public string Name => name;

        public ValueTask<ToolCallVerdict> ObserveAsync(
            ToolCallObservation observation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("rule engine unavailable");
    }

    private sealed class CancellingObserver : IToolCallObserver
    {
        public string Name => "cancels";

        public ValueTask<ToolCallVerdict> ObserveAsync(
            ToolCallObservation observation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ToolCallVerdict.Proceed());
        }
    }
}

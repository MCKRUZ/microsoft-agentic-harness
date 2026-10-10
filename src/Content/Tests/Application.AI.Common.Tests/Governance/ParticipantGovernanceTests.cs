using System.Runtime.CompilerServices;
using System.Text.Json;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Governance;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// A Magentic participant must be authorized as itself, not as the entry agent (#769). Microsoft's
/// workflow engine decides when each participant runs, so the only place to arm its governance is its
/// own <c>RunAsync</c>/<c>RunStreamingAsync</c>: these tests drive the wrapper that does that and
/// observe the admission pipeline a tool call made at that moment would reach.
/// </summary>
public sealed class ParticipantGovernanceTests : IDisposable
{
    private const string EntryAgent = "entry-agent";
    private const string Participant = "participant-1";
    private const string ConversationId = "conv-1";

    private readonly List<ArmedPipeline> _armed = [];
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _parentScope;
    private readonly IToolCallAdmissionPipeline _parentPipeline = Mock.Of<IToolCallAdmissionPipeline>();
    private readonly GovernanceTraceRecorder _parentTrace = new(
        Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == new GovernanceConfig()),
        Mock.Of<IToolRiskClassifier>());
    private readonly IAgentExecutionContext _parentContext;
    private readonly ParticipantGovernance _governance;
    private readonly ParticipantGovernanceTurn _turn;

    public ParticipantGovernanceTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();
        services.AddScoped<ScopeMarker>();
        services.AddScoped(sp =>
        {
            var armed = new ArmedPipeline(
                sp.GetRequiredService<IAgentExecutionContext>(), sp.GetRequiredService<ScopeMarker>());
            lock (_armed) _armed.Add(armed);
            return armed.Pipeline.Object;
        });
        _provider = services.BuildServiceProvider();

        _parentScope = _provider.CreateScope();
        _parentContext = _parentScope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();
        _parentContext.Initialize(EntryAgent, ConversationId, 3, ConversationId);

        _governance = new ParticipantGovernance(
            _provider.GetRequiredService<IServiceScopeFactory>(), _parentContext, _parentTrace);
        _turn = _governance.ForTurn(ConversationId);
    }

    public void Dispose()
    {
        _parentScope.Dispose();
        _provider.Dispose();
    }

    [Fact]
    public async Task RunAsync_ToolCallsInsideTheRunReachAPipelineArmedAsTheParticipant()
    {
        var probe = new ProbeAgent();
        var agent = _turn.Wrap(probe, Participant);

        using (ToolAdmissionAccessor.Begin(_parentPipeline))
            await agent.RunAsync("go");

        var seen = probe.PipelinesSeen.Should().ContainSingle().Subject;
        seen.Should().NotBeSameAs(_parentPipeline, "the entry agent's pipeline must not authorize the participant");
        var armed = ArmedFor(seen);
        armed.Context.AgentId.Should().Be(Participant);
        armed.Context.ConversationId.Should().Be(ConversationId, "a participant runs inside the supervisor's session");
        armed.Context.CallOnceScopeId.Should().Be(
            ConversationId, "a call-once tool the supervisor claimed must stay claimed for its participants");
    }

    [Fact]
    public async Task RunStreamingAsync_EveryStepOfTheStreamRunsUnderTheParticipantsPipeline()
    {
        // An async iterator does not carry ambient state across a yield, so a pipeline published once at
        // the top of the stream would govern only the first step; the wrapper has to publish it around
        // every step of the stream.
        var probe = new ProbeAgent(streamedUpdates: 3);
        var agent = _turn.Wrap(probe, Participant);

        using (ToolAdmissionAccessor.Begin(_parentPipeline))
        {
            await foreach (var _ in agent.RunStreamingAsync("go"))
            {
                ToolAdmissionAccessor.Current.Should().BeSameAs(
                    _parentPipeline, "the participant's pipeline must not leak out to the code consuming its stream");
            }
        }

        probe.PipelinesSeen.Should().HaveCount(3);
        probe.PipelinesSeen.Distinct().Should().ContainSingle("one run is one governance scope");
        ArmedFor(probe.PipelinesSeen[0]).Context.AgentId.Should().Be(Participant);
    }

    [Fact]
    public async Task RunsOfOneParticipantShareOneScope_AndANewTurnStartsFresh()
    {
        // The loop guard's call history and the aggregate output budget live in the scope's pipeline, so
        // they must span every round of a turn (#804): one scope per participant per turn, not per run.
        var agent = _turn.Wrap(new ProbeAgent(), Participant);

        await agent.RunAsync("round one");
        await agent.RunAsync("round two");

        var turnOne = _armed.Where(a => a.Context.AgentId == Participant).Should().ContainSingle(
            "both rounds must reach the same pipeline").Subject;
        turnOne.ResetCount.Should().Be(1, "state is cleared when the turn's scope is armed, not between rounds");

        await _turn.DisposeAsync();
        var nextTurn = _governance.ForTurn(ConversationId).Wrap(new ProbeAgent(), Participant);
        await nextTurn.RunAsync("next turn");

        _armed.Where(a => a.Context.AgentId == Participant).Should().HaveCount(
            2, "a new turn starts with fresh guard state");
    }

    [Fact]
    public async Task DisposingTheTurn_DisposesEveryParticipantScope_AndNotBefore()
    {
        var agent = _turn.Wrap(new ProbeAgent(), Participant);
        await agent.RunAsync("go");
        var armed = _armed.Single(a => a.Context.AgentId == Participant);

        armed.Marker.Disposed.Should().BeFalse("the scope must outlive the run so a later round reuses it");

        await _turn.DisposeAsync();

        armed.Marker.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task ARunAfterTheTurnEnded_IsRefused_InsteadOfArmingAScopeNobodyReleases()
    {
        // An engine that returns on cancellation without awaiting a participant can start it after the
        // runner ended the turn. A fresh scope then would never be folded or disposed.
        var agent = _turn.Wrap(new ProbeAgent(), Participant);
        await _turn.DisposeAsync();

        Func<Task> act = async () => await agent.RunAsync("too late");

        await act.Should().ThrowAsync<ObjectDisposedException>();
        _armed.Should().BeEmpty();
    }

    [Fact]
    public async Task ARunOfAParticipantWhoseScopeWasReleased_IsRefused()
    {
        var agent = _turn.Wrap(new ProbeAgent(), Participant);
        await agent.RunAsync("in time");
        await _turn.DisposeAsync();

        Func<Task> act = async () => await agent.RunAsync("too late");

        await act.Should().ThrowAsync<ObjectDisposedException>();
        _armed.Should().ContainSingle("the released scope must not be re-armed");
    }

    [Fact]
    public async Task Wrap_AfterTheTurnEnded_IsRefused()
    {
        await _turn.DisposeAsync();

        var act = () => _turn.Wrap(new ProbeAgent(), Participant);

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void ForTurn_RejectsAnEmptyConversationId()
    {
        var act = () => _governance.ForTurn("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task DisposingTheTurn_FoldsEachTraceOnce_EvenAcrossSeveralRounds()
    {
        // The pipeline's trace is cumulative, so folding after every round would double-count.
        var probe = new ProbeAgent(traceToReport: Trace("only-once"));
        var agent = _turn.Wrap(probe, Participant);

        await agent.RunAsync("round one");
        await agent.RunAsync("round two");

        _parentTrace.Snapshot().Should().BeSameAs(GovernanceTrace.Empty, "nothing is folded until the turn ends");

        await _turn.DisposeAsync();
        await _turn.DisposeAsync();

        _parentTrace.Snapshot().ToolDecisions.Should().ContainSingle(
            "one fold per turn, and disposing again has nothing left to fold");
    }

    [Fact]
    public async Task DisposingTheTurn_WhenOneParticipantsFoldFails_StillReleasesTheOthers()
    {
        var failing = _turn.Wrap(new ProbeAgent(), "failing-participant");
        var healthy = _turn.Wrap(new ProbeAgent(traceToReport: Trace("healthy-call")), "healthy-participant");
        await failing.RunAsync("go");
        await healthy.RunAsync("go");
        _armed.Single(a => a.Context.AgentId == "failing-participant").Pipeline
            .Setup(p => p.GetTrace()).Throws(new InvalidOperationException("trace unavailable"));

        Func<Task> act = async () => await _turn.DisposeAsync();

        await act.Should().ThrowAsync<AggregateException>();
        _armed.Single(a => a.Context.AgentId == "failing-participant").Marker.Disposed.Should().BeTrue(
            "a failed fold must still release that participant's scope");
        _armed.Single(a => a.Context.AgentId == "healthy-participant").Marker.Disposed.Should().BeTrue();
        _parentTrace.Snapshot().ToolDecisions.Should().ContainSingle()
            .Which.Reason.Should().Be("healthy-call");
    }

    [Fact]
    public async Task RunAsync_FoldsTheParticipantsTraceIntoTheTurnsTrace()
    {
        var probe = new ProbeAgent(traceToReport: Trace("participant-call"));
        var agent = _turn.Wrap(probe, Participant);

        await agent.RunAsync("go");
        await _turn.DisposeAsync();

        _parentTrace.Snapshot().ToolDecisions.Should().ContainSingle()
            .Which.Reason.Should().Be("participant-call");
    }

    [Fact]
    public async Task RunStreamingAsync_FoldsTheParticipantsTraceIntoTheTurnsTrace()
    {
        var probe = new ProbeAgent(streamedUpdates: 2, traceToReport: Trace("streamed-call"));
        var agent = _turn.Wrap(probe, Participant);

        await foreach (var _ in agent.RunStreamingAsync("go")) { }
        await _turn.DisposeAsync();

        _parentTrace.Snapshot().ToolDecisions.Should().ContainSingle()
            .Which.Reason.Should().Be("streamed-call");
    }

    [Fact]
    public async Task RunStreamingAsync_WhenDisposingTheStreamThrows_StillFoldsTheParticipantsTrace()
    {
        // A failing dispose must not strand the trace: the decisions a participant made, including denials,
        // are what an auditor reads, and an exception on the way out is exactly when they matter most.
        var probe = new ProbeAgent(traceToReport: Trace("before-the-dispose-failure"), throwOnDispose: true);
        var agent = _turn.Wrap(probe, Participant);

        // The consumer stops after the first update, so the stream is disposed by the wrapper rather than
        // exhausted: that is the path where the inner enumerator's dispose throws out of the wrapper's own.
        Func<Task> act = async () =>
        {
            await foreach (var _ in agent.RunStreamingAsync("go"))
                break;
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("dispose failed");
        await _turn.DisposeAsync();
        _parentTrace.Snapshot().ToolDecisions.Should().ContainSingle()
            .Which.Reason.Should().Be("before-the-dispose-failure");
    }

    [Fact]
    public async Task RunAsync_WhenTheParticipantThrows_StillFoldsItsTraceAndRestoresTheAmbientPipeline()
    {
        var probe = new ProbeAgent(throwAfterRecording: true, traceToReport: Trace("before-the-failure"));
        var agent = _turn.Wrap(probe, Participant);

        using (ToolAdmissionAccessor.Begin(_parentPipeline))
        {
            Func<Task> act = async () => await agent.RunAsync("go");

            await act.Should().ThrowAsync<InvalidOperationException>();
            ToolAdmissionAccessor.Current.Should().BeSameAs(_parentPipeline);
        }

        await _turn.DisposeAsync();
        _parentTrace.Snapshot().ToolDecisions.Should().ContainSingle(
            "a denial the participant hit before it failed is exactly what an auditor needs to see");
    }

    [Fact]
    public void TheWrappedAgentKeepsTheParticipantsIdentityAndName()
    {
        // Magentic addresses participants by name; a wrapper that lost it would break routing.
        var inner = new ProbeAgent();
        var agent = _turn.Wrap(inner, Participant);

        agent.Name.Should().Be(inner.Name);
        agent.Id.Should().Be(inner.Id);
    }

    [Fact]
    public void Wrap_RejectsABlankAgentId()
    {
        var act = () => _turn.Wrap(new ProbeAgent(), " ");

        act.Should().Throw<ArgumentException>();
    }

    private static GovernanceTrace Trace(string reason) => new()
    {
        EnforcementEnabled = true,
        ToolDecisions =
        [
            new ToolDecisionRecord(
                "file_system", ToolDecisionOutcome.Denied, reason, BlastRadius.Low,
                RequiredApproval: false, ApprovalGranted: false, Enforced: true),
        ],
    };

    private ArmedPipeline ArmedFor(IToolCallAdmissionPipeline pipeline) =>
        _armed.Single(a => ReferenceEquals(a.Pipeline.Object, pipeline));

    private sealed class ProbeSession : AgentSession
    {
    }

    /// <summary>Resolved inside each child scope; reports whether that scope has been disposed.</summary>
    private sealed class ScopeMarker : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A scope's pipeline mock plus the execution context that scope was armed with.</summary>
    private sealed class ArmedPipeline
    {
        public ArmedPipeline(IAgentExecutionContext context, ScopeMarker marker)
        {
            Context = context;
            Marker = marker;
            Pipeline = new Mock<IToolCallAdmissionPipeline>();
            Pipeline.Setup(p => p.Reset()).Callback(() => ResetCount++);
            Pipeline.Setup(p => p.GetTrace()).Returns(GovernanceTrace.Empty);
        }

        public IAgentExecutionContext Context { get; }
        public ScopeMarker Marker { get; }
        public Mock<IToolCallAdmissionPipeline> Pipeline { get; }
        public int ResetCount { get; private set; }
    }

    /// <summary>
    /// A participant that records the admission pipeline ambient at each point it would make a tool
    /// call — inside the run, where the framework's tool-calling layer runs — and can stream, fail, and
    /// report a trace the way a real governed scope would have accumulated one.
    /// </summary>
    private sealed class ProbeAgent : AIAgent
    {
        private readonly int _streamedUpdates;
        private readonly bool _throwOnDispose;
        private readonly bool _throwAfterRecording;
        private readonly GovernanceTrace? _traceToReport;
        private readonly List<IToolCallAdmissionPipeline> _seen = [];

        public ProbeAgent(
            int streamedUpdates = 1, bool throwAfterRecording = false, GovernanceTrace? traceToReport = null,
            bool throwOnDispose = false)
        {
            _throwOnDispose = throwOnDispose;
            _streamedUpdates = streamedUpdates;
            _throwAfterRecording = throwAfterRecording;
            _traceToReport = traceToReport;
        }

        public IReadOnlyList<IToolCallAdmissionPipeline> PipelinesSeen => _seen;

        protected override string IdCore => "probe-id";
        public override string? Name => "probe-name";
        public override string? Description => "probe";

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            CancellationToken cancellationToken)
        {
            Record();
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            CancellationToken cancellationToken) =>
            _throwOnDispose ? StreamThenThrowOnDispose() : Stream(cancellationToken);

        /// <summary>Yields once; an async iterator's finally runs on dispose, so an early stop throws from it.</summary>
        private async IAsyncEnumerable<AgentResponseUpdate> StreamThenThrowOnDispose()
        {
            try
            {
                await Task.Yield();
                Record();
                yield return new AgentResponseUpdate(ChatRole.Assistant, "only");
            }
            finally
            {
                throw new InvalidOperationException("dispose failed");
            }
        }

        private async IAsyncEnumerable<AgentResponseUpdate> Stream([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 0; i < _streamedUpdates; i++)
            {
                await Task.Yield();
                Record();
                yield return new AgentResponseUpdate(ChatRole.Assistant, $"update-{i}");
            }
        }

        private void Record()
        {
            var pipeline = ToolAdmissionAccessor.Current!;
            _seen.Add(pipeline);

            // What a real scope's recorder would hold by now: stand it on the armed pipeline's mock.
            if (_traceToReport is not null)
                Mock.Get(pipeline).Setup(p => p.GetTrace()).Returns(_traceToReport);

            if (_throwAfterRecording)
                throw new InvalidOperationException("participant failed");
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<AgentSession>(new ProbeSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken)
            => ValueTask.FromResult(JsonDocument.Parse("{}").RootElement);

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<AgentSession>(new ProbeSession());
    }
}

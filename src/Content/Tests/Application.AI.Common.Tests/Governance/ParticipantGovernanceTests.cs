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

    public ParticipantGovernanceTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();
        services.AddScoped(sp =>
        {
            var armed = new ArmedPipeline(sp.GetRequiredService<IAgentExecutionContext>());
            lock (_armed) _armed.Add(armed);
            return armed.Pipeline.Object;
        });
        _provider = services.BuildServiceProvider();

        _parentScope = _provider.CreateScope();
        _parentContext = _parentScope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();
        _parentContext.Initialize(EntryAgent, ConversationId, 3, ConversationId);

        _governance = new ParticipantGovernance(
            _provider.GetRequiredService<IServiceScopeFactory>(), _parentContext, _parentTrace);
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
        var agent = _governance.Wrap(probe, Participant, ConversationId);

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
        var agent = _governance.Wrap(probe, Participant, ConversationId);

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
    public async Task EveryRunGetsAFreshScopeWithAResetPipeline()
    {
        var agent = _governance.Wrap(new ProbeAgent(), Participant, ConversationId);

        await agent.RunAsync("one");
        await agent.RunAsync("two");

        _armed.Where(a => a.Context.AgentId == Participant).Should().HaveCount(2)
            .And.OnlyContain(a => a.ResetCount == 1);
    }

    [Fact]
    public async Task RunAsync_FoldsTheParticipantsTraceIntoTheTurnsTrace()
    {
        var probe = new ProbeAgent(traceToReport: Trace("participant-call"));
        var agent = _governance.Wrap(probe, Participant, ConversationId);

        await agent.RunAsync("go");

        _parentTrace.Snapshot().ToolDecisions.Should().ContainSingle()
            .Which.Reason.Should().Be("participant-call");
    }

    [Fact]
    public async Task RunStreamingAsync_FoldsTheParticipantsTraceIntoTheTurnsTrace()
    {
        var probe = new ProbeAgent(streamedUpdates: 2, traceToReport: Trace("streamed-call"));
        var agent = _governance.Wrap(probe, Participant, ConversationId);

        await foreach (var _ in agent.RunStreamingAsync("go")) { }

        _parentTrace.Snapshot().ToolDecisions.Should().ContainSingle()
            .Which.Reason.Should().Be("streamed-call");
    }

    [Fact]
    public async Task RunStreamingAsync_WhenDisposingTheStreamThrows_StillFoldsTheParticipantsTrace()
    {
        // A failing dispose must not strand the trace: the decisions a participant made, including denials,
        // are what an auditor reads, and an exception on the way out is exactly when they matter most.
        var probe = new ProbeAgent(traceToReport: Trace("before-the-dispose-failure"), throwOnDispose: true);
        var agent = _governance.Wrap(probe, Participant, ConversationId);

        // The consumer stops after the first update, so the stream is disposed by the wrapper rather than
        // exhausted: that is the path where the inner enumerator's dispose throws out of the wrapper's own.
        Func<Task> act = async () =>
        {
            await foreach (var _ in agent.RunStreamingAsync("go"))
                break;
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("dispose failed");
        _parentTrace.Snapshot().ToolDecisions.Should().ContainSingle()
            .Which.Reason.Should().Be("before-the-dispose-failure");
    }

    [Fact]
    public async Task RunAsync_WhenTheParticipantThrows_StillFoldsItsTraceAndRestoresTheAmbientPipeline()
    {
        var probe = new ProbeAgent(throwAfterRecording: true, traceToReport: Trace("before-the-failure"));
        var agent = _governance.Wrap(probe, Participant, ConversationId);

        using (ToolAdmissionAccessor.Begin(_parentPipeline))
        {
            Func<Task> act = async () => await agent.RunAsync("go");

            await act.Should().ThrowAsync<InvalidOperationException>();
            ToolAdmissionAccessor.Current.Should().BeSameAs(_parentPipeline);
        }

        _parentTrace.Snapshot().ToolDecisions.Should().ContainSingle(
            "a denial the participant hit before it failed is exactly what an auditor needs to see");
    }

    [Fact]
    public async Task TheWrappedAgentKeepsTheParticipantsIdentityAndName()
    {
        // Magentic addresses participants by name; a wrapper that lost it would break routing.
        var inner = new ProbeAgent();
        var agent = _governance.Wrap(inner, Participant, ConversationId);

        agent.Name.Should().Be(inner.Name);
        agent.Id.Should().Be(inner.Id);
        await Task.CompletedTask;
    }

    [Fact]
    public void Wrap_RejectsABlankAgentId()
    {
        var act = () => _governance.Wrap(new ProbeAgent(), " ", ConversationId);

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

    /// <summary>A scope's pipeline mock plus the execution context that scope was armed with.</summary>
    private sealed class ArmedPipeline
    {
        public ArmedPipeline(IAgentExecutionContext context)
        {
            Context = context;
            Pipeline = new Mock<IToolCallAdmissionPipeline>();
            Pipeline.Setup(p => p.Reset()).Callback(() => ResetCount++);
            Pipeline.Setup(p => p.GetTrace()).Returns(GovernanceTrace.Empty);
        }

        public IAgentExecutionContext Context { get; }
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
            _throwOnDispose ? new ThrowOnDisposeStream(Record) : Stream(cancellationToken);

        /// <summary>A stream that yields once and then throws when it is disposed.</summary>
        private sealed class ThrowOnDisposeStream(Action record) : IAsyncEnumerable<AgentResponseUpdate>
        {
            public IAsyncEnumerator<AgentResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
                new Enumerator(record);

            private sealed class Enumerator(Action record) : IAsyncEnumerator<AgentResponseUpdate>
            {
                private bool _yielded;

                public AgentResponseUpdate Current { get; private set; } = new(ChatRole.Assistant, "only");

                public ValueTask<bool> MoveNextAsync()
                {
                    if (_yielded)
                        return ValueTask.FromResult(false);

                    _yielded = true;
                    record();
                    return ValueTask.FromResult(true);
                }

                public ValueTask DisposeAsync() => throw new InvalidOperationException("dispose failed");
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

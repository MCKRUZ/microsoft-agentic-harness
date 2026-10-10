using System.Text.Json;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Governance;
using Infrastructure.AI.Orchestration.Magentic;
using Domain.Common.Config.AI;
using Infrastructure.AI.Tests.Helpers;
using Infrastructure.AI.Tests.Planner.StepExecutors;
using Infrastructure.AI.Tests.Support;
using FluentAssertions;
using MediatR;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

#pragma warning disable MAAIW001

namespace Infrastructure.AI.Tests.Orchestration.Magentic;

/// <summary>
/// Drives the real <see cref="MagenticOrchestrator"/> against the real MAF Magentic workflow, with
/// scripted agents standing in for the models. Every other Magentic test either mocks the
/// orchestrator or feeds the subscriber hand-built events, so nothing before this file ran the
/// run loop itself — which is how a run that was never given its task shipped.
/// </summary>
/// <remarks>
/// MAF's Magentic orchestrator only starts when it has been sent both the input messages and a
/// <see cref="TurnToken"/> (it does not start itself), and <c>InProcessExecution.OpenStreamingAsync</c>
/// sends neither. These tests pin that the harness supplies them and that the run's outcome is
/// reported honestly.
/// </remarks>
[Collection("MagenticTraceCollection")]
public sealed class MagenticOrchestratorRunTests
{
    private const string TaskText = "write the quarterly report";

    // A manager reply that says the request is satisfied; the scripted manager returns it for every prompt.
    private const string SatisfiedLedger = """
        {"is_request_satisfied":{"reason":"done","answer":true},
         "is_in_loop":{"reason":"no","answer":false},
         "is_progress_being_made":{"reason":"yes","answer":true},
         "next_speaker":{"reason":"only one","answer":"researcher"},
         "instruction_or_question":{"reason":"go","answer":"research it"}}
        """;

    [Fact]
    public async Task RunAsync_OpensRun_SendsTheRequestTaskToTheManager()
    {
        var manager = new ScriptedAgent("manager", ScriptedBehavior.RecordThenFail);
        var request = BuildRequest(manager);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await BuildOrchestrator().RunAsync(request, cts.Token);

        manager.ReceivedText.Should().Contain(
            t => t.Contains(TaskText, StringComparison.Ordinal),
            "the manager plans from the task; a run that never delivers it just waits for input");
    }

    [Fact]
    public async Task RunAsync_WhenCallerCancels_ReportsFailureNotSuccess()
    {
        var manager = new ScriptedAgent("manager", ScriptedBehavior.BlockUntilCancelled);
        var request = BuildRequest(manager);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = BuildOrchestrator().RunAsync(request, cts.Token);
        await manager.Started.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        var result = await run;

        result.IsSuccess.Should().BeFalse(
            "MAF's stream reader swallows cancellation, so the run ends quietly; reporting that as a "
            + "satisfied completion hands the caller an empty answer as if it were a real one");
    }

    [Fact]
    public async Task RunAsync_AmbientAdmissionPipeline_ReachesTheAgentsRunningInsideTheWorkflow()
    {
        // GovernedAIFunction fails open when no admission pipeline is on the current async flow, and
        // MAF runs the workflow on a task it starts itself. Governance of every Magentic tool call
        // therefore depends on that task inheriting the caller's execution context; this pins it.
        var manager = new ScriptedAgent("manager", ScriptedBehavior.RecordThenFail);
        var request = BuildRequest(manager);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using (ToolAdmissionAccessor.Begin(Mock.Of<IToolCallAdmissionPipeline>()))
        {
            await BuildOrchestrator().RunAsync(request, cts.Token);
        }

        manager.SawAdmissionPipeline.Should().BeTrue(
            "a tool call made by the manager or a participant must still be admitted by the caller's pipeline");
    }

    [Fact]
    public async Task RunAsync_AParticipantTheManagerSelects_RunsUnderItsOwnPipeline_WhileTheManagerKeepsTheTurns()
    {
        // #769 end to end, through the real workflow engine: the participant the manager hands work to is
        // authorized as itself, and the manager — the entry agent — still runs under the turn's pipeline.
        // Only a run through the engine can show which pipeline is ambient when IT decides to run an agent.
        const string ContinueToResearcher = """
            {"is_request_satisfied":{"reason":"not yet","answer":false},
             "is_in_loop":{"reason":"no","answer":false},
             "is_progress_being_made":{"reason":"yes","answer":true},
             "next_speaker":{"reason":"needs research","answer":"researcher"},
             "instruction_or_question":{"reason":"go","answer":"research it"}}
            """;
        var turnPipeline = new Mock<IToolCallAdmissionPipeline>().Object;
        var manager = new ScriptedAgent(
            "manager", ScriptedBehavior.Reply, "facts", "plan", ContinueToResearcher, SatisfiedLedger, "the final answer");
        var researcher = new ScriptedAgent("researcher", ScriptedBehavior.Reply, "researched");

        var scopeFactory = FakeGovernanceScopeFactory.Create(out _, out var childPipeline);
        childPipeline.Setup(p => p.GetTrace()).Returns(Domain.AI.Governance.GovernanceTrace.Empty);
        var governance = new ParticipantGovernance(
            scopeFactory,
            Mock.Of<IAgentExecutionContext>(c => c.ConversationId == "conv-1"),
            new GovernanceTraceRecorder(
                Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == new GovernanceConfig()),
                Mock.Of<IToolRiskClassifier>()));
        await using var governanceTurn = governance.ForTurn("conv-1");
        var request = BuildRequest(manager) with { Participants = [governanceTurn.Wrap(researcher, "researcher")] };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        MagenticWorkflowResult? outcome;
        using (ToolAdmissionAccessor.Begin(turnPipeline))
            outcome = (await BuildOrchestrator().RunAsync(request, cts.Token)).Value;

        researcher.PipelinesSeen.Should().NotBeEmpty("the manager selected the researcher, so it ran");
        researcher.PipelinesSeen.Should().OnlyContain(
            p => ReferenceEquals(p, childPipeline.Object),
            "every tool call the participant makes must reach a pipeline armed as the participant");
        manager.PipelinesSeen.Should().NotBeEmpty().And.OnlyContain(
            p => ReferenceEquals(p, turnPipeline),
            "the manager is the entry agent and keeps the turn's own pipeline");
        outcome!.FinalOutput.Should().Contain("the final answer");
    }

    [Fact]
    public async Task RunAsync_ManagerEndsWithoutAnAnswer_FailsRatherThanReportingSatisfied()
    {
        // The manager's last reply (its final answer) is blank, so the run completes with nothing to
        // report. Calling that "satisfied" would hand the caller an empty answer as a real one.
        var manager = new ScriptedAgent("manager", ScriptedBehavior.Reply, SatisfiedLedger, SatisfiedLedger, SatisfiedLedger, "");
        var participant = new ScriptedAgent("researcher", ScriptedBehavior.Reply, "researched");
        var request = BuildRequest(manager) with { Participants = [participant] };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await BuildOrchestrator().RunAsync(request, cts.Token);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("magentic.no_final_output");
    }

    [Fact]
    public async Task RunAsync_ManagerSatisfiedOnFirstRound_ReturnsTheManagersFinalAnswer()
    {
        // Drives a real Magentic workflow to its terminal output with a manager that answers every
        // prompt with a "request satisfied" ledger, so the shape of the terminal output is measured
        // against the framework rather than assumed from hand-built events.
        var manager = new ScriptedAgent("manager", ScriptedBehavior.Reply, SatisfiedLedger);
        var participant = new ScriptedAgent("researcher", ScriptedBehavior.Reply, "researched");
        var request = BuildRequest(manager) with { Participants = [participant] };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await BuildOrchestrator().RunAsync(request, cts.Token);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.FinalOutput.Should().Contain(
            "is_request_satisfied",
            "the answer is the manager's own final message, not a type name or a participant's reply");
    }

    private static MagenticWorkflowRequest BuildRequest(AIAgent manager) => new()
    {
        Manager = manager,
        Participants = [new ScriptedAgent("researcher", ScriptedBehavior.RecordThenFail)],
        Task = TaskText,
        Name = "run-loop-test",
        WorkflowId = Guid.NewGuid(),
        MaxStalls = 3,
        RequirePlanSignoff = false
    };

    private static MagenticOrchestrator BuildOrchestrator()
    {
        var mediator = new Mock<IMediator>();
        var router = new MagenticChangeProposalRouter(
            TestScopeFactory.For(mediator.Object),
            NullLogger<MagenticChangeProposalRouter>.Instance);

        return new MagenticOrchestrator(
            new MagenticSpanEmitter(),
            Mock.Of<IMagenticPlanReviewBridge>(),
            router,
            Mock.Of<IContentCapturePolicy>(),
            PermissiveAdmission.PermissiveSanitizer(),
            Mock.Of<IContentRedactionFilter>(),
            NullLoggerFactory.Instance);
    }

    private enum ScriptedBehavior { RecordThenFail, BlockUntilCancelled, Reply }

    /// <summary>An agent that records what it is asked, then fails fast or blocks, so a test never needs a model.</summary>
    private sealed class ScriptedAgent : AIAgent
    {
        private readonly string _id;
        private readonly ScriptedBehavior _behavior;
        private readonly List<string> _received = [];

        private readonly string[] _replies;
        private int _calls;

        /// <param name="replies">Replies in call order for <see cref="ScriptedBehavior.Reply"/>; the last repeats.</param>
        public ScriptedAgent(string id, ScriptedBehavior behavior, params string[] replies)
        {
            _id = id;
            _behavior = behavior;
            _replies = replies.Length == 0 ? [""] : replies;
        }

        private string NextReply() => _replies[Math.Min(Interlocked.Increment(ref _calls) - 1, _replies.Length - 1)];

        public IReadOnlyList<string> ReceivedText
        {
            get { lock (_received) return [.. _received]; }
        }

        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the workflow first invokes this agent, so a test can act on a run that is demonstrably under way.</summary>
        public Task Started => _started.Task;

        private readonly List<IToolCallAdmissionPipeline> _pipelinesSeen = [];

        /// <summary>True when an admission pipeline was visible on the thread the workflow ran this agent on.</summary>
        public bool SawAdmissionPipeline => PipelinesSeen.Count > 0;

        /// <summary>The admission pipeline that was ambient each time the workflow ran this agent.</summary>
        public IReadOnlyList<IToolCallAdmissionPipeline> PipelinesSeen
        {
            get { lock (_pipelinesSeen) return [.. _pipelinesSeen]; }
        }

        protected override string IdCore => _id;
        public override string? Name => _id;
        public override string? Description => _id;

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => new(new TestableAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken)
            => new(JsonDocument.Parse("{}").RootElement.Clone());

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken)
            => new(new TestableAgentSession());

        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken)
        {
            await RecordAndMisbehaveAsync(messages, cancellationToken).ConfigureAwait(false);
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, NextReply()));
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await RecordAndMisbehaveAsync(messages, cancellationToken).ConfigureAwait(false);
            yield return new AgentResponseUpdate(ChatRole.Assistant, NextReply());
        }

        private async Task RecordAndMisbehaveAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
        {
            if (ToolAdmissionAccessor.Current is { } ambient)
                lock (_pipelinesSeen) _pipelinesSeen.Add(ambient);

            lock (_received)
                _received.AddRange(messages.Select(m => m.Text));

            _started.TrySetResult();

            if (_behavior == ScriptedBehavior.BlockUntilCancelled)
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);

            if (_behavior == ScriptedBehavior.RecordThenFail)
                throw new InvalidOperationException("scripted agent stops the run after recording");
        }
    }
}

#pragma warning restore MAAIW001

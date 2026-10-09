using System.Text.Json;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Governance;
using Infrastructure.AI.Orchestration.Magentic;
using Infrastructure.AI.Tests.Helpers;
using Infrastructure.AI.Tests.Planner.StepExecutors;
using Infrastructure.AI.Tests.Support;
using FluentAssertions;
using MediatR;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
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
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        var result = await BuildOrchestrator().RunAsync(request, cts.Token);

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

    private enum ScriptedBehavior { RecordThenFail, BlockUntilCancelled }

    /// <summary>An agent that records what it is asked, then fails fast or blocks, so a test never needs a model.</summary>
    private sealed class ScriptedAgent : AIAgent
    {
        private readonly string _id;
        private readonly ScriptedBehavior _behavior;
        private readonly List<string> _received = [];

        public ScriptedAgent(string id, ScriptedBehavior behavior)
        {
            _id = id;
            _behavior = behavior;
        }

        public IReadOnlyList<string> ReceivedText
        {
            get { lock (_received) return [.. _received]; }
        }

        /// <summary>True when the caller's admission pipeline was visible on the thread the workflow ran this agent on.</summary>
        public bool SawAdmissionPipeline { get; private set; }

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
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, "unreachable"));
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await RecordAndMisbehaveAsync(messages, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        private async Task RecordAndMisbehaveAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
        {
            if (ToolAdmissionAccessor.Current is not null) SawAdmissionPipeline = true;

            lock (_received)
                _received.AddRange(messages.Select(m => m.Text));

            if (_behavior == ScriptedBehavior.BlockUntilCancelled)
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);

            throw new InvalidOperationException("scripted agent stops the run after recording");
        }
    }
}

#pragma warning restore MAAIW001

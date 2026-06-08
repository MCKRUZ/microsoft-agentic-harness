using System.Diagnostics;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Conventions;
using Infrastructure.AI.Orchestration.Magentic;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

#pragma warning disable MAAIW001

namespace Infrastructure.AI.Tests.Orchestration.Magentic;

/// <summary>
/// Shared fixtures + helpers for the PR-6 Magentic test suite. Owns the
/// <see cref="ActivityListener"/> collection contract so every test sees the
/// same span semantics.
/// </summary>
internal static class MagenticTestHelpers
{
    public sealed class CapturedSpans : IDisposable
    {
        private readonly ActivityListener _listener;
        public List<Activity> Activities { get; } = new();

        public CapturedSpans()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == MagenticConventions.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = a => Activities.Add(a)
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }

    public static MagenticWorkflowRequest BuildRequest(
        bool requirePlanSignoff = true,
        int? maxRounds = null,
        int maxStalls = 3,
        int? maxResets = null)
    {
        var manager = MakeAgent("manager");
        var participants = new[] { MakeAgent("participant-a"), MakeAgent("participant-b") };
        return new MagenticWorkflowRequest
        {
            Manager = manager,
            Participants = participants,
            Task = "do thing",
            Name = "test-workflow",
            WorkflowId = Guid.NewGuid(),
            MaxRounds = maxRounds,
            MaxStalls = maxStalls,
            MaxResets = maxResets,
            RequirePlanSignoff = requirePlanSignoff
        };
    }

    private static Microsoft.Agents.AI.AIAgent MakeAgent(string name) => new FakeAgent(name);

    private sealed class FakeAgent : Microsoft.Agents.AI.AIAgent
    {
        private readonly string _id;
        public FakeAgent(string id) { _id = id; }
        protected override string IdCore => _id;
        public override string? Name => _id;
        public override string? Description => _id;
        protected override System.Threading.Tasks.ValueTask<Microsoft.Agents.AI.AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
        protected override System.Threading.Tasks.ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(Microsoft.Agents.AI.AgentSession session, System.Text.Json.JsonSerializerOptions jsonSerializerOptions, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        protected override System.Threading.Tasks.ValueTask<Microsoft.Agents.AI.AgentSession> DeserializeSessionCoreAsync(System.Text.Json.JsonElement serializedState, System.Text.Json.JsonSerializerOptions jsonSerializerOptions, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        protected override System.Threading.Tasks.Task<Microsoft.Agents.AI.AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, Microsoft.Agents.AI.AgentSession session, Microsoft.Agents.AI.AgentRunOptions options, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        protected override IAsyncEnumerable<Microsoft.Agents.AI.AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, Microsoft.Agents.AI.AgentSession session, Microsoft.Agents.AI.AgentRunOptions options, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    public static MagenticEventSubscriber BuildSubscriber(
        out Mock<IMagenticPlanReviewBridge> bridge,
        out Mock<IMediator> mediator,
        MagenticSpanEmitter? emitter = null,
        IContentCapturePolicy? capturePolicy = null,
        IContentRedactionFilter? redactionFilter = null)
    {
        bridge = new Mock<IMagenticPlanReviewBridge>();
        mediator = new Mock<IMediator>();
        var router = new MagenticChangeProposalRouter(mediator.Object, NullLogger<MagenticChangeProposalRouter>.Instance);
        return new MagenticEventSubscriber(
            emitter ?? new MagenticSpanEmitter(),
            bridge.Object,
            router,
            // Default to capture-off mocks so pre-content-capture tests are
            // unaffected; ShouldCapture* on a bare mock returns false.
            capturePolicy ?? Mock.Of<IContentCapturePolicy>(),
            redactionFilter ?? Mock.Of<IContentRedactionFilter>(),
            NullLogger<MagenticEventSubscriber>.Instance);
    }

    public static ChatMessage AsLedger(string text) => new(ChatRole.Assistant, text);

    /// <summary>
    /// Build a <see cref="Microsoft.Agents.AI.Workflows.MagenticProgressLedger"/>
    /// via reflection — the public ctor is internal and the setters are
    /// internal, so tests cannot use the standard <c>with</c> syntax. Mirrors the
    /// shape MAF emits on a real coordination round.
    /// </summary>
    public static Microsoft.Agents.AI.Workflows.MagenticProgressLedger BuildLedger(
        bool isStarted = true,
        bool isRequestSatisfied = false,
        bool isInLoop = false,
        bool isProgressBeingMade = true,
        string nextSpeaker = "participant-a",
        string instructionOrQuestion = "next")
    {
        var t = typeof(Microsoft.Agents.AI.Workflows.MagenticProgressLedger);
        var ctor = t.GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).First();
        var ps = ctor.GetParameters();
        // (string teamNames, IEnumerable<ProgressLedgerSlot> additionalQuestions, JsonElement? state)
        var emptySlots = Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(ps[1].ParameterType.GetGenericArguments()[0]));
        var ledger = (Microsoft.Agents.AI.Workflows.MagenticProgressLedger)ctor.Invoke(new[] { (object?)string.Empty, emptySlots, null });
        Set("IsStarted", isStarted);
        Set("IsRequestSatisfied", isRequestSatisfied);
        Set("IsInLoop", isInLoop);
        Set("IsProgressBeingMade", isProgressBeingMade);
        Set("NextSpeaker", nextSpeaker);
        Set("InstructionOrQuestion", instructionOrQuestion);
        return ledger;

        void Set(string name, object value)
        {
            var prop = t.GetProperty(name)!;
            var setter = prop.GetSetMethod(nonPublic: true);
            if (setter is null)
            {
                // IsStarted has no setter — back it via the backing field directly.
                var field = t.GetField($"<{name}>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                field?.SetValue(ledger, value);
                return;
            }
            setter.Invoke(ledger, new[] { value });
        }
    }
}

#pragma warning restore MAAIW001

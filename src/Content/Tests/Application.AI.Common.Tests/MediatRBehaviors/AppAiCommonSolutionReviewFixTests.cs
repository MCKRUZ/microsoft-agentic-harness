using Application.AI.Common.CQRS.SkillTraining.SlowUpdate;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.MediatRBehaviors;
using Application.AI.Common.Middleware;
using Application.AI.Common.Models;
using Application.AI.Common.Services.Skills;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.SkillTraining;
using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

/// <summary>
/// Regression tests for solution-review findings 53, 55, and 21 in the
/// <c>Application.AI.Common</c> lane.
/// </summary>
public sealed class AppAiCommonSolutionReviewFixTests
{
    // ---------------------------------------------------------------------
    // Finding 55: SlowUpdate ToDictionary throws on duplicate ItemIds.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SlowUpdate_DuplicateItemIds_DoesNotThrowAndUsesLastWins()
    {
        // Arrange — two rollouts share ItemId "a". The old ToDictionary(...) would throw
        // ArgumentException here and abort the whole epoch-boundary run.
        var handler = new SlowUpdateCommandHandler();
        var command = new SlowUpdateCommand
        {
            PriorRollouts =
            [
                Rollout("a", success: false),
                Rollout("a", success: false), // duplicate
                Rollout("b", success: false),
            ],
            CurrentRollouts =
            [
                Rollout("a", success: true), // last-wins: improved
                Rollout("b", success: true), // improved
            ],
        };

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert — no throw, and the last prior rollout for "a" (failure) paired against the
        // current success counts as an improvement.
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Improved);
        Assert.Equal(0, result.Value.Regressed);
    }

    private static RolloutResult Rollout(string itemId, bool success) => new()
    {
        ItemId = itemId,
        Hard = success ? 1.0 : 0.0,
        Soft = success ? 1.0 : 0.0,
    };

    // ---------------------------------------------------------------------
    // Finding 53: streaming path never detects completion tools.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task StreamingResponse_CompletionToolInvoked_MarksSkillComplete()
    {
        // Arrange — "validate" completes when its completion tool "run_validation" is called.
        var map = new SkillPrerequisiteMap
        {
            Skills = new Dictionary<string, SkillPrerequisiteEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["validate"] = new SkillPrerequisiteEntry
                {
                    SkillId = "validate",
                    Prerequisites = [],
                    CompletionTool = "run_validation",
                    ToolNames = ["run_validation"],
                },
            },
        };
        var tracker = new InMemorySkillCompletionTracker();

        var streamingInner = new StreamingToolCallChatClient("run_validation");
        var middleware = new SkillPrerequisiteMiddleware(
            streamingInner, tracker, map, "conv-stream",
            NullLogger<SkillPrerequisiteMiddleware>.Instance);

        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok", "run_validation")] };

        // Act — drain the streaming response.
        await foreach (var _ in middleware.GetStreamingResponseAsync([], options))
        {
        }

        // Assert — the streaming path must mark the skill complete, mirroring GetResponseAsync.
        Assert.True(tracker.IsCompleted("conv-stream", "validate"));
    }

    private sealed class StreamingToolCallChatClient : IChatClient
    {
        private readonly string _toolName;

        public StreamingToolCallChatClient(string toolName) => _toolName = toolName;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "thinking ");
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new FunctionCallContent(Guid.NewGuid().ToString(), _toolName)],
            };
            await Task.CompletedTask;
        }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    // ---------------------------------------------------------------------
    // Finding 21: KnowledgeExtractionBehavior must resolve services from a
    // fresh DI scope (not capture request-scoped instances) for the post-turn write.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task KnowledgeExtraction_PostTurnWrite_ResolvesFromFreshScopeAndPersists()
    {
        // Arrange — register the extractor + memory as SCOPED so that a captured-instance
        // implementation would observe a disposed scope, whereas the fixed implementation
        // resolves them from a fresh scope created inside the background task.
        var memory = new RecordingKnowledgeMemory();
        var extractor = new SingleFactExtractor();

        var services = new ServiceCollection();
        services.AddScoped<IConversationFactExtractor>(_ => extractor);
        services.AddScoped<IKnowledgeMemory>(_ => memory);
        var rootProvider = services.BuildServiceProvider();

        var config = Options.Create(new KnowledgeBridgeConfig
        {
            Enabled = true,
            ExtractionTimeoutSeconds = 30,
        });

        var behavior = new KnowledgeExtractionBehavior<FakeTurnRequest, FakeTurnResult>(
            rootProvider.GetRequiredService<IServiceScopeFactory>(),
            new TestAmbientRequestScope(),
            config,
            NullLogger<KnowledgeExtractionBehavior<FakeTurnRequest, FakeTurnResult>>.Instance);

        var request = new FakeTurnRequest();
        var result = new FakeTurnResult();

        // Act — run the behavior; the post-turn write is fire-and-forget.
        var returned = await behavior.Handle(request, () => Task.FromResult(result), CancellationToken.None);

        // Assert — response returned immediately, and the background write eventually persists.
        Assert.Same(result, returned);
        await memory.WaitForWriteAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("fact-key", memory.PersistedKeys);
    }

    private sealed class FakeTurnRequest : IAgentTurnRequest
    {
        public string AgentId => "agent-1";
        public string ConversationId => "conv-21";
        public int TurnNumber => 1;
        public string UserMessage => "remember this";
    }

    private sealed class FakeTurnResult : IAgentTurnResult
    {
        public bool Success => true;
        public string Response => "noted";
        public int InputTokens => 0;
        public int OutputTokens => 0;
    }

    private sealed class SingleFactExtractor : IConversationFactExtractor
    {
        public Task<IReadOnlyList<ConversationFact>> ExtractAsync(
            string userMessage,
            string assistantResponse,
            string conversationId,
            int turnNumber,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ConversationFact> facts =
            [
                new ConversationFact { Key = "fact-key", Content = "a fact", EntityType = "Fact" },
            ];
            return Task.FromResult(facts);
        }
    }

    private sealed class RecordingKnowledgeMemory : IKnowledgeMemory
    {
        private readonly TaskCompletionSource _written =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _persistedKeys = [];
        private readonly object _gate = new();

        public IReadOnlyList<string> PersistedKeys
        {
            get { lock (_gate) { return _persistedKeys.ToList(); } }
        }

        public Task RememberAsync(
            string key,
            string content,
            string entityType = "Fact",
            CancellationToken cancellationToken = default)
        {
            lock (_gate) { _persistedKeys.Add(key); }
            _written.TrySetResult();
            return Task.CompletedTask;
        }

        public Task WaitForWriteAsync(TimeSpan timeout)
            => _written.Task.WaitAsync(timeout);

        public Task<IReadOnlyList<GraphNode>> RecallAsync(
            string query, int maxResults = 5, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GraphNode>>([]);

        public Task ForgetAsync(string key, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ImproveAsync(
            string userMessage,
            string assistantResponse,
            IReadOnlyList<string> relevantNodeIds,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class TestAmbientRequestScope : IAmbientRequestScope
    {
        public IServiceProvider? Current { get; private set; }

        public IDisposable BeginScope(IServiceProvider requestServices)
        {
            var previous = Current;
            Current = requestServices;
            return new Token(this, previous);
        }

        private sealed class Token : IDisposable
        {
            private readonly TestAmbientRequestScope _owner;
            private readonly IServiceProvider? _previous;

            public Token(TestAmbientRequestScope owner, IServiceProvider? previous)
            {
                _owner = owner;
                _previous = previous;
            }

            public void Dispose() => _owner.Current = _previous;
        }
    }
}

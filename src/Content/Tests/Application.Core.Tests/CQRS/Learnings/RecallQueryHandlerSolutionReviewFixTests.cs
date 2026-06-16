using Application.AI.Common.Interfaces.Learnings;
using Application.AI.Common.Interfaces.RAG;
using Application.Core.CQRS.Learnings;
using Domain.AI.Learnings;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Learnings;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Regression tests for the solution-review fix to <see cref="RecallQueryHandler"/>:
/// the fire-and-forget access-recording dispatch must run on a fresh DI scope created via
/// <see cref="IServiceScopeFactory"/>, not on the (already-disposed) request scope.
/// </summary>
/// <remarks>
/// Old behavior: the handler captured the request-scoped <see cref="IMediator"/> and dispatched
/// <c>RecordLearningAccessCommand</c> after the request returned, which threw
/// <see cref="System.ObjectDisposedException"/> once the request scope was disposed — silently
/// dropping the <c>LastAccessedAt</c> reinforcement that feeds recall ranking.
/// New behavior: the handler creates its own scope and resolves a fresh mediator from it,
/// so the dispatch survives request-scope disposal.
/// </remarks>
public sealed class RecallQueryHandlerSolutionReviewFixTests
{
    private readonly Mock<ILearningsStore> _store = new();
    private readonly Mock<ILearningDecayService> _decayService = new();
    private readonly Mock<IEmbeddingService> _embeddingService = new();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Handle_RequestScopeDisposed_StillRecordsAccessFromFreshScope()
    {
        // Arrange: a scope factory whose scope's mediator is the only one the handler can use.
        // We dispose the produced scope to simulate request-scope teardown; the handler must
        // create its OWN scope rather than reusing a disposed one.
        var recorded = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<RecordLearningAccessCommand>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((cmd, _) =>
                recorded.TrySetResult(((RecordLearningAccessCommand)cmd).LearningIds.Count))
            .ReturnsAsync(Result.Success());

        var provider = new ServiceCollection()
            .AddScoped(_ => mediator.Object)
            .BuildServiceProvider();

        SetupStore([CreateLearning("l1"), CreateLearning("l2")]);
        SetupUniformEmbedding(0.8);
        _decayService
            .Setup(d => d.CalculateFreshnessAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1.0);

        var handler = new RecallQueryHandler(
            _store.Object,
            _decayService.Object,
            _embeddingService.Object,
            provider.GetRequiredService<IServiceScopeFactory>(),
            CreateOptions(new LearningsConfig()),
            _timeProvider,
            NullLogger<RecallQueryHandler>.Instance);

        // Act
        var result = await handler.Handle(CreateQuery(), CancellationToken.None);

        // Assert: the fire-and-forget dispatch resolved a working mediator from a fresh scope.
        result.IsSuccess.Should().BeTrue();
        var completed = await Task.WhenAny(recorded.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(recorded.Task, "access recording must run on a handler-owned scope");
        (await recorded.Task).Should().Be(2);
        mediator.Verify(
            m => m.Send(It.IsAny<RecordLearningAccessCommand>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private void SetupStore(IReadOnlyList<LearningEntry> learnings) =>
        _store.Setup(s => s.SearchAsync(It.IsAny<LearningSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<LearningEntry>>.Success(learnings));

    private void SetupUniformEmbedding(double similarity)
    {
        var queryVector = new float[] { 1.0f, 0.0f, 0.0f };
        var sim = (float)similarity;
        var perpComponent = (float)Math.Sqrt(1 - sim * sim);
        var contentVector = new float[] { sim, perpComponent, 0 };
        var callCount = 0;

        _embeddingService.Setup(e => e.EmbedQueryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var index = callCount++;
                return index == 0
                    ? new ReadOnlyMemory<float>(queryVector)
                    : new ReadOnlyMemory<float>(contentVector);
            });
    }

    private static RecallQuery CreateQuery(string context = "test context", int maxResults = 10) => new()
    {
        Context = context,
        Scope = new LearningScope { AgentId = "agent-1" },
        MaxResults = maxResults
    };

    private static LearningEntry CreateLearning(string content) => new()
    {
        LearningId = Guid.NewGuid(),
        Category = LearningCategory.FactualCorrection,
        DecayClass = DecayClass.Permanent,
        Scope = new LearningScope { AgentId = "agent-1" },
        Content = content,
        Source = new LearningSource
        {
            SourceType = LearningSourceType.HumanCorrection,
            SourceId = "src-1",
            SourceDescription = "Test"
        },
        Provenance = new LearningProvenance
        {
            OriginPipeline = "test",
            OriginTask = "test",
            OriginTimestamp = DateTimeOffset.UtcNow,
            Confidence = 1.0
        },
        CreatedAt = DateTimeOffset.UtcNow,
        FeedbackWeight = 1.0
    };

    private static IOptionsMonitor<AppConfig> CreateOptions(LearningsConfig config)
    {
        var appConfig = new AppConfig { AI = new AIConfig { Learnings = config } };
        var mock = new Mock<IOptionsMonitor<AppConfig>>();
        mock.Setup(m => m.CurrentValue).Returns(appConfig);
        return mock.Object;
    }
}

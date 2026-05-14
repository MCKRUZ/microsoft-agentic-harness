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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Tests for <see cref="RecallQueryHandler"/>.
/// </summary>
public sealed class RecallQueryHandlerTests
{
    private readonly Mock<ILearningsStore> _store = new();
    private readonly Mock<ILearningDecayService> _decayService = new();
    private readonly Mock<IEmbeddingService> _embeddingService = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly LearningsConfig _config = new();

    public RecallQueryHandlerTests()
    {
        _mediator.Setup(m => m.Send(It.IsAny<RecordLearningAccessCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
    }

    [Fact]
    public async Task Handle_MatchingLearnings_ReturnsSortedByFinalScore()
    {
        var learnings = new List<LearningEntry>
        {
            CreateLearning("high relevance"),
            CreateLearning("medium relevance"),
            CreateLearning("low relevance")
        };

        SetupStore(learnings);
        SetupEmbeddingsWithSimilarities([0.9, 0.5, 0.7]);
        _decayService.Setup(d => d.CalculateFreshnessAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1.0);

        var handler = CreateHandler();
        var result = await handler.Handle(CreateQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(3);
        result.Value[0].RelevanceScore.Should().BeApproximately(0.9, 0.01);
        result.Value[0].FinalScore.Should().BeGreaterThan(result.Value[1].FinalScore);
    }

    [Fact]
    public async Task Handle_FeedbackCeiling_CapsInfluence()
    {
        var learning = CreateLearning("high feedback", feedbackWeight: 5.0);
        SetupStore([learning]);
        SetupUniformEmbedding(0.8);
        _decayService.Setup(d => d.CalculateFreshnessAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1.0);

        var config = new LearningsConfig { FeedbackAlpha = 0.25, FeedbackCeiling = 0.3 };
        var handler = CreateHandler(config);
        var result = await handler.Handle(CreateQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var wl = result.Value![0];
        var expectedFinal = (1 - 0.25) * 0.8 + 0.25 * Math.Min(5.0 * 1.0, 0.3);
        wl.FinalScore.Should().BeApproximately(expectedFinal, 0.01);
    }

    [Fact]
    public async Task Handle_FiresRecordLearningAccessCommand()
    {
        var learnings = new List<LearningEntry> { CreateLearning("l1"), CreateLearning("l2") };
        SetupStore(learnings);
        SetupUniformEmbedding(0.8);
        _decayService.Setup(d => d.CalculateFreshnessAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1.0);

        var handler = CreateHandler();
        await handler.Handle(CreateQuery(), CancellationToken.None);

        _mediator.Verify(
            m => m.Send(It.Is<RecordLearningAccessCommand>(c => c.LearningIds.Count == 2), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_EmptyResults_ReturnsEmptyList()
    {
        _store.Setup(s => s.SearchAsync(It.IsAny<LearningSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<LearningEntry>>.Success(Array.Empty<LearningEntry>()));

        var handler = CreateHandler();
        var result = await handler.Handle(CreateQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_UsesEmbeddingServiceForRelevance()
    {
        SetupStore([CreateLearning("test")]);
        SetupUniformEmbedding(0.5);
        _decayService.Setup(d => d.CalculateFreshnessAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1.0);

        var handler = CreateHandler();
        await handler.Handle(CreateQuery("my query"), CancellationToken.None);

        _embeddingService.Verify(
            e => e.EmbedQueryAsync("my query", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_Disabled_ReturnsSuccessEmptyList()
    {
        var handler = CreateHandler(new LearningsConfig { Enabled = false });
        var result = await handler.Handle(CreateQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
        _store.Verify(s => s.SearchAsync(It.IsAny<LearningSearchCriteria>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DiversityInjection_SkippedWhenTooFewResults()
    {
        SetupStore([CreateLearning("only one")]);
        SetupUniformEmbedding(0.8);
        _decayService.Setup(d => d.CalculateFreshnessAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1.0);

        var handler = CreateHandler(new LearningsConfig { DiversityInjectionRatio = 0.15 });
        var result = await handler.Handle(CreateQuery(maxResults: 10), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
    }

    private RecallQueryHandler CreateHandler(LearningsConfig? config = null) => new(
        _store.Object,
        _decayService.Object,
        _embeddingService.Object,
        _mediator.Object,
        CreateOptions(config ?? _config),
        _timeProvider,
        NullLogger<RecallQueryHandler>.Instance);

    private void SetupStore(IReadOnlyList<LearningEntry> learnings)
    {
        _store.Setup(s => s.SearchAsync(It.IsAny<LearningSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<LearningEntry>>.Success(learnings));
    }

    private void SetupEmbeddingsWithSimilarities(double[] similarities)
    {
        var queryVector = CreateVector(1.0f, 0.0f, 0.0f);
        var callCount = 0;

        _embeddingService.Setup(e => e.EmbedQueryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var index = callCount++;
                if (index == 0) return new ReadOnlyMemory<float>(queryVector);

                var sim = (float)similarities[index - 1];
                var perpComponent = (float)Math.Sqrt(1 - sim * sim);
                return new ReadOnlyMemory<float>([sim, perpComponent, 0]);
            });
    }

    private void SetupUniformEmbedding(double similarity)
    {
        var queryVector = CreateVector(1.0f, 0.0f, 0.0f);
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

    private static float[] CreateVector(float x, float y, float z) => [x, y, z];

    private static RecallQuery CreateQuery(string context = "test context", int maxResults = 10) => new()
    {
        Context = context,
        Scope = new LearningScope { AgentId = "agent-1" },
        MaxResults = maxResults
    };

    private static LearningEntry CreateLearning(string content, double feedbackWeight = 1.0) => new()
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
        FeedbackWeight = feedbackWeight
    };

    private static IOptionsMonitor<AppConfig> CreateOptions(LearningsConfig config)
    {
        var appConfig = new AppConfig { AI = new AIConfig { Learnings = config } };
        var mock = new Mock<IOptionsMonitor<AppConfig>>();
        mock.Setup(m => m.CurrentValue).Returns(appConfig);
        return mock.Object;
    }
}

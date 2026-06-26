using Application.Core.CQRS.Learnings;
using Application.Core.Learnings;
using Domain.AI.Learnings;
using Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.Learnings;

public sealed class MediatorLearningRecallerTests
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly MediatorLearningRecaller _sut;

    public MediatorLearningRecallerTests()
        => _sut = new MediatorLearningRecaller(_mediator.Object, NullLogger<MediatorLearningRecaller>.Instance);

    [Fact]
    public async Task RecallAsync_DispatchesRecallQuery_WithGlobalScopeAndParams()
    {
        RecallQuery? captured = null;
        _mediator
            .Setup(m => m.Send(It.IsAny<RecallQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<IReadOnlyList<WeightedLearning>>>, CancellationToken>((q, _) => captured = (RecallQuery)q)
            .ReturnsAsync(Result<IReadOnlyList<WeightedLearning>>.Success([]));

        await _sut.RecallAsync("deploy the app", maxResults: 5, minRelevance: 0.4, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Context.Should().Be("deploy the app");
        captured.MaxResults.Should().Be(5);
        captured.MinRelevance.Should().Be(0.4);
        captured.Scope.IsGlobal.Should().BeTrue();
    }

    [Fact]
    public async Task RecallAsync_Success_ReturnsLearnings()
    {
        var lessons = new[] { Weighted("lesson A"), Weighted("lesson B") };
        _mediator
            .Setup(m => m.Send(It.IsAny<RecallQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<WeightedLearning>>.Success(lessons));

        var result = await _sut.RecallAsync("ctx", 3, 0.3, CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Learning.Content.Should().Be("lesson A");
    }

    [Fact]
    public async Task RecallAsync_Failure_ReturnsEmpty()
    {
        _mediator
            .Setup(m => m.Send(It.IsAny<RecallQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<WeightedLearning>>.Fail("recall offline"));

        var result = await _sut.RecallAsync("ctx", 3, 0.3, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RecallAsync_BlankContext_ReturnsEmptyWithoutDispatching(string context)
    {
        var result = await _sut.RecallAsync(context, 3, 0.3, CancellationToken.None);

        result.Should().BeEmpty();
        _mediator.Verify(m => m.Send(It.IsAny<RecallQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static WeightedLearning Weighted(string content) => new()
    {
        Learning = new LearningEntry
        {
            LearningId = Guid.NewGuid(),
            Category = LearningCategory.DomainKnowledge,
            DecayClass = DecayClass.Stable,
            Scope = new LearningScope { IsGlobal = true },
            Content = content,
            Source = new LearningSource
            {
                SourceType = LearningSourceType.AgentSelfImprovement,
                SourceId = "run-1",
                SourceDescription = "synthesis"
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = "p",
                OriginTask = "t",
                OriginTimestamp = DateTimeOffset.UtcNow,
                Confidence = 0.9
            },
            CreatedAt = DateTimeOffset.UtcNow
        },
        RelevanceScore = 0.8,
        FeedbackScore = 1.0,
        FreshnessScore = 1.0,
        FinalScore = 0.85
    };
}

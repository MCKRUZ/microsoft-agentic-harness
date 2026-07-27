using Application.Core.CQRS.Learnings;
using Domain.AI.Learnings;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Verifies <see cref="RecallLearningsQueryHandler"/>: it must forward to the inner
/// <see cref="RecallQuery"/> with global scope and the caller's context/limits, and propagate
/// the inner result unchanged — success values pass through, and failures surface as failures
/// so the HTTP layer can report store errors honestly instead of masking them as empty results
/// (the deliberate difference from <c>MediatorLearningRecaller</c>, which swallows failures).
/// </summary>
public sealed class RecallLearningsQueryHandlerTests
{
    private const double ConfiguredMinRelevance = 0.4;

    private readonly Mock<IMediator> _mediator = new();
    private readonly RecallLearningsQueryHandler _sut;

    public RecallLearningsQueryHandlerTests() =>
        _sut = new RecallLearningsQueryHandler(_mediator.Object, CreateOptions(ConfiguredMinRelevance));

    private static IOptionsMonitor<AppConfig> CreateOptions(double minRelevance)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                LearningsRecall = new LearningsRecallConfig { MinRelevance = minRelevance }
            }
        };
        var mock = new Mock<IOptionsMonitor<AppConfig>>();
        mock.Setup(m => m.CurrentValue).Returns(appConfig);
        return mock.Object;
    }

    [Fact]
    public async Task Handle_ValidQuery_ForwardsContextAndMaxResultsWithGlobalScope()
    {
        RecallQuery? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<RecallQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<IReadOnlyList<WeightedLearning>>>, CancellationToken>(
                (q, _) => captured = (RecallQuery)q)
            .ReturnsAsync(Result<IReadOnlyList<WeightedLearning>>.Success(Array.Empty<WeightedLearning>()));

        await _sut.Handle(
            new RecallLearningsQuery { Context = "deploy checklist", MaxResults = 7 },
            CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Context.Should().Be("deploy checklist");
        captured.MaxResults.Should().Be(7);
        captured.MinRelevance.Should().Be(ConfiguredMinRelevance,
            "the HTTP surface must apply the same configured relevance floor as the agent-turn recall");
        captured.RecordAccess.Should().BeFalse(
            "an HTTP GET must never trigger the access-reinforcement store write");
        captured.Scope.IsGlobal.Should().BeTrue(
            "the HTTP recall must mirror the global scope the in-process agent-turn recall uses");
        captured.Scope.AgentId.Should().BeNull();
        captured.Scope.TeamId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_InnerSuccess_PropagatesValueUnchanged()
    {
        IReadOnlyList<WeightedLearning> learnings = [CreateWeighted()];
        _mediator.Setup(m => m.Send(It.IsAny<RecallQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<WeightedLearning>>.Success(learnings));

        var result = await _sut.Handle(
            new RecallLearningsQuery { Context = "anything" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(learnings);
    }

    [Fact]
    public async Task Handle_InnerFailure_PropagatesFailure()
    {
        _mediator.Setup(m => m.Send(It.IsAny<RecallQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<WeightedLearning>>.Fail("store unavailable"));

        var result = await _sut.Handle(
            new RecallLearningsQuery { Context = "anything" }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse(
            "store failures must surface as failures, not as an empty 200 body");
        result.Errors.Should().Contain("store unavailable");
    }

    private static WeightedLearning CreateWeighted() => new()
    {
        Learning = new LearningEntry
        {
            LearningId = Guid.NewGuid(),
            Category = LearningCategory.DomainKnowledge,
            DecayClass = DecayClass.Stable,
            Scope = new LearningScope { IsGlobal = true },
            Content = "Prefer Result<T> over exceptions for expected failures",
            Source = new LearningSource
            {
                SourceType = LearningSourceType.HumanCorrection,
                SourceId = "session-123",
                SourceDescription = "User corrected error-handling approach"
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = "drift-detection",
                OriginTask = "auto_correct",
                OriginTimestamp = DateTimeOffset.UtcNow,
                Confidence = 0.9
            },
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-3)
        },
        RelevanceScore = 0.8,
        FeedbackScore = 1.2,
        FreshnessScore = 0.9,
        FinalScore = 0.85
    };
}

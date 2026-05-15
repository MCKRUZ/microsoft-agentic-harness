using Application.AI.Common.Interfaces.Learnings;
using Application.Core.CQRS.Learnings;
using Domain.AI.Learnings;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Learnings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Tests for <see cref="ImproveLearningCommandHandler"/>.
/// </summary>
public sealed class ImproveLearningCommandHandlerTests
{
    private readonly Mock<ILearningsStore> _store = new();
    private readonly Mock<ILearningsDriftBridge> _driftBridge = new();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero));

    public ImproveLearningCommandHandlerTests()
    {
        _driftBridge
            .Setup(b => b.CheckAndAdjustBaselineAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
    }

    [Fact]
    public async Task Handle_AppliesEmaToFeedbackWeight()
    {
        var learning = CreateLearning(feedbackWeight: 1.0, updateCount: 0);
        SetupGetAndUpdate(learning);
        var config = new LearningsConfig { FeedbackAlpha = 0.25, BiasCorrection = false };
        var handler = CreateHandler(config);

        var result = await handler.Handle(
            new ImproveLearningCommand { LearningId = learning.LearningId, FeedbackScore = 4.0 },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // normalized = (4.0 - 1.0) / 4.0 = 0.75
        // newWeight = 0.25 * 0.75 + 0.75 * 1.0 = 0.9375
        result.Value!.FeedbackWeight.Should().BeApproximately(0.9375, 0.001);
    }

    [Fact]
    public async Task Handle_EmaCalculation_KnownInputs()
    {
        var learning = CreateLearning(feedbackWeight: 0.6, updateCount: 3);
        SetupGetAndUpdate(learning);
        var config = new LearningsConfig { FeedbackAlpha = 0.25, BiasCorrection = false };
        var handler = CreateHandler(config);

        var result = await handler.Handle(
            new ImproveLearningCommand { LearningId = learning.LearningId, FeedbackScore = 5.0 },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // normalized = (5.0 - 1.0) / 4.0 = 1.0
        // newWeight = 0.25 * 1.0 + 0.75 * 0.6 = 0.7
        result.Value!.FeedbackWeight.Should().BeApproximately(0.7, 0.001);
    }

    [Fact]
    public async Task Handle_BiasCorrection_NewLearning()
    {
        var learning = CreateLearning(feedbackWeight: 1.0, updateCount: 1);
        SetupGetAndUpdate(learning);
        var config = new LearningsConfig { FeedbackAlpha = 0.25, BiasCorrection = true };
        var handler = CreateHandler(config);

        var result = await handler.Handle(
            new ImproveLearningCommand { LearningId = learning.LearningId, FeedbackScore = 4.0 },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // normalized = 0.75
        // rawWeight = 0.25 * 0.75 + 0.75 * 1.0 = 0.9375
        // correctionFactor = 1 / (1 - (0.75)^2) = 1 / (1 - 0.5625) = 1 / 0.4375 ≈ 2.2857
        // corrected = clamp(0.9375 * 2.2857, 0, 1) = clamp(2.1429, 0, 1) = 1.0
        result.Value!.FeedbackWeight.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public async Task Handle_BiasCorrection_Disabled_NoAdjustment()
    {
        var learning = CreateLearning(feedbackWeight: 1.0, updateCount: 1);
        SetupGetAndUpdate(learning);
        var config = new LearningsConfig { FeedbackAlpha = 0.25, BiasCorrection = false };
        var handler = CreateHandler(config);

        var result = await handler.Handle(
            new ImproveLearningCommand { LearningId = learning.LearningId, FeedbackScore = 4.0 },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // Without bias correction: 0.25 * 0.75 + 0.75 * 1.0 = 0.9375
        result.Value!.FeedbackWeight.Should().BeApproximately(0.9375, 0.001);
    }

    [Fact]
    public async Task Handle_IncrementsUpdateCount()
    {
        var learning = CreateLearning(updateCount: 2);
        SetupGetAndUpdate(learning);
        var handler = CreateHandler();

        var result = await handler.Handle(
            new ImproveLearningCommand { LearningId = learning.LearningId, FeedbackScore = 3.0 },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.UpdateCount.Should().Be(3);
    }

    [Fact]
    public async Task Handle_SetsLastReinforcedAt()
    {
        var learning = CreateLearning();
        SetupGetAndUpdate(learning);
        var handler = CreateHandler();

        var result = await handler.Handle(
            new ImproveLearningCommand { LearningId = learning.LearningId, FeedbackScore = 3.0 },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.LastReinforcedAt.Should().Be(_timeProvider.GetUtcNow());
    }

    [Fact]
    public async Task Handle_LearningNotFound_ReturnsNotFound()
    {
        var learningId = Guid.NewGuid();
        _store.Setup(s => s.GetAsync(learningId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<LearningEntry?>.Success(null));
        var handler = CreateHandler();

        var result = await handler.Handle(
            new ImproveLearningCommand { LearningId = learningId, FeedbackScore = 3.0 },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public async Task Handle_Disabled_ReturnsSuccessNoOp()
    {
        var handler = CreateHandler(new LearningsConfig { Enabled = false });

        var result = await handler.Handle(
            new ImproveLearningCommand { LearningId = Guid.NewGuid(), FeedbackScore = 3.0 },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _store.Verify(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private ImproveLearningCommandHandler CreateHandler(LearningsConfig? config = null) => new(
        _store.Object,
        _driftBridge.Object,
        CreateOptions(config ?? new LearningsConfig { BiasCorrection = false }),
        _timeProvider,
        NullLogger<ImproveLearningCommandHandler>.Instance);

    private void SetupGetAndUpdate(LearningEntry learning)
    {
        _store.Setup(s => s.GetAsync(learning.LearningId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<LearningEntry?>.Success(learning));
        _store.Setup(s => s.UpdateAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
    }

    private static LearningEntry CreateLearning(
        double feedbackWeight = 1.0,
        int updateCount = 0) => new()
    {
        LearningId = Guid.NewGuid(),
        Category = LearningCategory.FactualCorrection,
        DecayClass = DecayClass.Permanent,
        Scope = new LearningScope { AgentId = "agent-1" },
        Content = "Test content",
        Source = new LearningSource
        {
            SourceType = LearningSourceType.DriftDetection,
            SourceId = "drift-1",
            SourceDescription = "Test drift"
        },
        Provenance = new LearningProvenance
        {
            OriginPipeline = "test",
            OriginTask = "test",
            OriginTimestamp = DateTimeOffset.UtcNow,
            Confidence = 1.0
        },
        CreatedAt = DateTimeOffset.UtcNow,
        FeedbackWeight = feedbackWeight,
        UpdateCount = updateCount
    };

    [Fact]
    public async Task Handle_SuccessfulUpdate_CallsBridge()
    {
        var learning = CreateLearning(feedbackWeight: 0.9);
        SetupGetAndUpdate(learning);
        var handler = CreateHandler();

        await handler.Handle(
            new ImproveLearningCommand { LearningId = learning.LearningId, FeedbackScore = 5.0 },
            CancellationToken.None);

        _driftBridge.Verify(
            b => b.CheckAndAdjustBaselineAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_BridgeFailure_StillReturnsSuccess()
    {
        var learning = CreateLearning(feedbackWeight: 0.9);
        SetupGetAndUpdate(learning);
        _driftBridge
            .Setup(b => b.CheckAndAdjustBaselineAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Fail("Bridge error"));
        var handler = CreateHandler();

        var result = await handler.Handle(
            new ImproveLearningCommand { LearningId = learning.LearningId, FeedbackScore = 5.0 },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_Disabled_DoesNotCallBridge()
    {
        var handler = CreateHandler(new LearningsConfig { Enabled = false });

        await handler.Handle(
            new ImproveLearningCommand { LearningId = Guid.NewGuid(), FeedbackScore = 3.0 },
            CancellationToken.None);

        _driftBridge.Verify(
            b => b.CheckAndAdjustBaselineAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static IOptionsMonitor<AppConfig> CreateOptions(LearningsConfig config)
    {
        var appConfig = new AppConfig { AI = new AIConfig { Learnings = config } };
        var mock = new Mock<IOptionsMonitor<AppConfig>>();
        mock.Setup(m => m.CurrentValue).Returns(appConfig);
        return mock.Object;
    }
}

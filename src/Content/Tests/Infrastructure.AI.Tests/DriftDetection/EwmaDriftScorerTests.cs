using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.DriftDetection;
using FluentAssertions;
using Infrastructure.AI.DriftDetection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.DriftDetection;

public sealed class EwmaDriftScorerTests
{
    private readonly Mock<IEwmaStateStore> _stateStoreMock = new();
    private readonly Mock<ILogger<EwmaDriftScorer>> _loggerMock = new();
    private readonly FakeTimeProvider _timeProvider = new();

    private EwmaDriftScorer CreateScorer(double lambda = 0.2, bool enabled = true)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                DriftDetection = new DriftDetectionConfig
                {
                    Enabled = enabled,
                    EwmaLambda = lambda,
                    ControlLimitWidth = 3.0,
                    WarnThresholdSigma = 1.5,
                    AlertThresholdSigma = 2.5,
                    EscalateThresholdSigma = 3.0
                }
            }
        };

        var optionsMonitor = Mock.Of<IOptionsMonitor<AppConfig>>(
            o => o.CurrentValue == appConfig);

        return new EwmaDriftScorer(
            _stateStoreMock.Object,
            optionsMonitor,
            _timeProvider,
            _loggerMock.Object);
    }

    private static DriftBaseline CreateBaseline(
        double mean = 0.8, double sigma = 0.1, DriftDimension dimension = DriftDimension.Faithfulness)
    {
        return new DriftBaseline
        {
            BaselineId = Guid.NewGuid(),
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Dimensions = new Dictionary<DriftDimension, double> { [dimension] = mean }.AsReadOnly(),
            DimensionSigmas = new Dictionary<DriftDimension, double> { [dimension] = sigma }.AsReadOnly(),
            SampleCount = 30,
            WindowStart = DateTimeOffset.UtcNow.AddDays(-7),
            WindowEnd = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    [Fact]
    public async Task ScoreDimension_FirstEvaluation_InitializesFromBaselineMean()
    {
        // Arrange
        var baseline = CreateBaseline(mean: 0.8, sigma: 0.1);

        _stateStoreMock
            .Setup(s => s.GetStateAsync(DriftScope.Skill, "code_review", DriftDimension.Faithfulness, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result<EwmaState?>.Success(null));

        _stateStoreMock
            .Setup(s => s.SaveStateAsync(It.IsAny<EwmaState>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result.Success());

        var scorer = CreateScorer(lambda: 0.2);

        // Act — currentValue=0.75, no prior state, so previousEwma = baselineMean = 0.8
        // newEwma = 0.2 * 0.75 + 0.8 * 0.8 = 0.15 + 0.64 = 0.79
        var result = await scorer.ScoreDimensionAsync(
            DriftDimension.Faithfulness, 0.75, baseline, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.EwmaValue.Should().BeApproximately(0.79, 1e-10);
        result.Value.CurrentValue.Should().Be(0.75);
        result.Value.BaselineValue.Should().Be(0.8);

        _stateStoreMock.Verify(s => s.SaveStateAsync(
            It.Is<EwmaState>(st => st.SampleCount == 1 && Math.Abs(st.CurrentEwma - 0.79) < 1e-10),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScoreDimension_SubsequentEvaluation_AppliesEwmaFormula()
    {
        // Arrange
        var baseline = CreateBaseline(mean: 0.8, sigma: 0.1);
        var existingState = new EwmaState
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Dimension = DriftDimension.Faithfulness,
            CurrentEwma = 0.79,
            SampleCount = 1,
            LastUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        _stateStoreMock
            .Setup(s => s.GetStateAsync(DriftScope.Skill, "code_review", DriftDimension.Faithfulness, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result<EwmaState?>.Success(existingState));

        _stateStoreMock
            .Setup(s => s.SaveStateAsync(It.IsAny<EwmaState>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result.Success());

        var scorer = CreateScorer(lambda: 0.2);

        // Act — currentValue=0.7, previousEwma=0.79
        // newEwma = 0.2 * 0.7 + 0.8 * 0.79 = 0.14 + 0.632 = 0.772
        var result = await scorer.ScoreDimensionAsync(
            DriftDimension.Faithfulness, 0.7, baseline, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.EwmaValue.Should().BeApproximately(0.772, 1e-10);

        _stateStoreMock.Verify(s => s.SaveStateAsync(
            It.Is<EwmaState>(st => st.SampleCount == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScoreDimension_KnownInputs_ProducesExpectedEwma()
    {
        // Arrange — 5 sequential observations: [0.8, 0.75, 0.7, 0.65, 0.6]
        // Starting from baseline mean = 0.8, lambda = 0.2
        // Step 1: EWMA = 0.2*0.8  + 0.8*0.8  = 0.80000
        // Step 2: EWMA = 0.2*0.75 + 0.8*0.80 = 0.79000
        // Step 3: EWMA = 0.2*0.70 + 0.8*0.79 = 0.77200
        // Step 4: EWMA = 0.2*0.65 + 0.8*0.772= 0.74760
        // Step 5: EWMA = 0.2*0.60 + 0.8*0.7476= 0.71808

        var baseline = CreateBaseline(mean: 0.8, sigma: 0.1);
        var observations = new[] { 0.8, 0.75, 0.7, 0.65, 0.6 };
        var expectedFinalEwma = 0.71808;

        EwmaState? currentState = null;

        _stateStoreMock
            .Setup(s => s.GetStateAsync(DriftScope.Skill, "code_review", DriftDimension.Faithfulness, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Domain.Common.Result<EwmaState?>.Success(currentState));

        _stateStoreMock
            .Setup(s => s.SaveStateAsync(It.IsAny<EwmaState>(), It.IsAny<CancellationToken>()))
            .Callback<EwmaState, CancellationToken>((state, _) => currentState = state)
            .ReturnsAsync(Domain.Common.Result.Success());

        var scorer = CreateScorer(lambda: 0.2);

        // Act — feed all observations sequentially
        Domain.Common.Result<DriftDimensionScore>? result = null;
        foreach (var value in observations)
        {
            result = await scorer.ScoreDimensionAsync(
                DriftDimension.Faithfulness, value, baseline, CancellationToken.None);
        }

        // Assert
        result!.IsSuccess.Should().BeTrue();
        result.Value!.EwmaValue.Should().BeApproximately(expectedFinalEwma, 1e-6);
    }

    [Fact]
    public async Task ScoreDimension_LambdaZeroPointTwo_WeightsHistoryEightyPercent()
    {
        // Arrange — prior EWMA=0.8, currentValue=0.0
        // newEwma = 0.2 * 0.0 + 0.8 * 0.8 = 0.64
        var baseline = CreateBaseline(mean: 0.8, sigma: 0.1);
        var existingState = new EwmaState
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Dimension = DriftDimension.Faithfulness,
            CurrentEwma = 0.8,
            SampleCount = 10,
            LastUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        _stateStoreMock
            .Setup(s => s.GetStateAsync(DriftScope.Skill, "code_review", DriftDimension.Faithfulness, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result<EwmaState?>.Success(existingState));

        _stateStoreMock
            .Setup(s => s.SaveStateAsync(It.IsAny<EwmaState>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result.Success());

        var scorer = CreateScorer(lambda: 0.2);

        // Act
        var result = await scorer.ScoreDimensionAsync(
            DriftDimension.Faithfulness, 0.0, baseline, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.EwmaValue.Should().BeApproximately(0.64, 1e-10);
    }

    [Fact]
    public async Task ScoreDimension_DeviationCalculation_CorrectSigmaUnits()
    {
        // Arrange — baseline mean=0.8, sigma=0.1, EWMA=0.55
        // deviation = |0.55 - 0.8| / 0.1 = 2.5
        var baseline = CreateBaseline(mean: 0.8, sigma: 0.1);
        var existingState = new EwmaState
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Dimension = DriftDimension.Faithfulness,
            CurrentEwma = 0.8,
            SampleCount = 10,
            LastUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        _stateStoreMock
            .Setup(s => s.GetStateAsync(DriftScope.Skill, "code_review", DriftDimension.Faithfulness, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result<EwmaState?>.Success(existingState));

        _stateStoreMock
            .Setup(s => s.SaveStateAsync(It.IsAny<EwmaState>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result.Success());

        var scorer = CreateScorer(lambda: 0.2);

        // Act — feed value that makes EWMA land at 0.55
        // We need: 0.2 * x + 0.8 * 0.8 = 0.55 => 0.2x = 0.55 - 0.64 = -0.09 => x = -0.45
        // Deviation = |0.55 - 0.8| / 0.1 = 2.5
        var result = await scorer.ScoreDimensionAsync(
            DriftDimension.Faithfulness, -0.45, baseline, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.EwmaValue.Should().BeApproximately(0.55, 1e-10);
        result.Value.Deviation.Should().BeApproximately(2.5, 1e-10);
    }

    [Fact]
    public async Task ScoreDimension_ZeroVariance_ReturnsZeroDeviation()
    {
        // Arrange — sigma=0 should produce deviation=0, no division by zero
        var baseline = CreateBaseline(mean: 0.8, sigma: 0.0);

        _stateStoreMock
            .Setup(s => s.GetStateAsync(DriftScope.Skill, "code_review", DriftDimension.Faithfulness, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result<EwmaState?>.Success(null));

        _stateStoreMock
            .Setup(s => s.SaveStateAsync(It.IsAny<EwmaState>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result.Success());

        var scorer = CreateScorer(lambda: 0.2);

        // Act
        var result = await scorer.ScoreDimensionAsync(
            DriftDimension.Faithfulness, 0.5, baseline, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Deviation.Should().Be(0.0);
    }

    [Fact]
    public async Task ScoreDimension_SavesUpdatedEwmaState()
    {
        // Arrange
        var baseline = CreateBaseline(mean: 0.8, sigma: 0.1);

        _stateStoreMock
            .Setup(s => s.GetStateAsync(DriftScope.Skill, "code_review", DriftDimension.Faithfulness, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result<EwmaState?>.Success(null));

        _stateStoreMock
            .Setup(s => s.SaveStateAsync(It.IsAny<EwmaState>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result.Success());

        var scorer = CreateScorer();

        // Act
        await scorer.ScoreDimensionAsync(
            DriftDimension.Faithfulness, 0.75, baseline, CancellationToken.None);

        // Assert
        _stateStoreMock.Verify(s => s.SaveStateAsync(
            It.Is<EwmaState>(st =>
                st.Scope == DriftScope.Skill &&
                st.ScopeIdentifier == "code_review" &&
                st.Dimension == DriftDimension.Faithfulness &&
                st.SampleCount == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScoreDimension_LoadsExistingEwmaState()
    {
        // Arrange
        var baseline = CreateBaseline(mean: 0.8, sigma: 0.1);

        _stateStoreMock
            .Setup(s => s.GetStateAsync(DriftScope.Skill, "code_review", DriftDimension.Faithfulness, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result<EwmaState?>.Success(null));

        _stateStoreMock
            .Setup(s => s.SaveStateAsync(It.IsAny<EwmaState>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result.Success());

        var scorer = CreateScorer();

        // Act
        await scorer.ScoreDimensionAsync(
            DriftDimension.Faithfulness, 0.75, baseline, CancellationToken.None);

        // Assert — verifies the correct state was queried
        _stateStoreMock.Verify(s => s.GetStateAsync(
            DriftScope.Skill, "code_review", DriftDimension.Faithfulness,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScoreDimension_UsesTimeProviderForTimestamp()
    {
        // Arrange
        var now = new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(now);

        var baseline = CreateBaseline(mean: 0.8, sigma: 0.1);

        _stateStoreMock
            .Setup(s => s.GetStateAsync(DriftScope.Skill, "code_review", DriftDimension.Faithfulness, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result<EwmaState?>.Success(null));

        _stateStoreMock
            .Setup(s => s.SaveStateAsync(It.IsAny<EwmaState>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result.Success());

        var scorer = CreateScorer();

        // Act
        await scorer.ScoreDimensionAsync(
            DriftDimension.Faithfulness, 0.75, baseline, CancellationToken.None);

        // Assert
        _stateStoreMock.Verify(s => s.SaveStateAsync(
            It.Is<EwmaState>(st => st.LastUpdatedAt == now),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScoreDimension_DisabledConfig_ReturnsSuccessNoOp()
    {
        // Arrange
        var baseline = CreateBaseline();
        var scorer = CreateScorer(enabled: false);

        // Act
        var result = await scorer.ScoreDimensionAsync(
            DriftDimension.Faithfulness, 0.5, baseline, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Deviation.Should().Be(0.0);
        result.Value.EwmaValue.Should().Be(0.0);
        result.Value.CurrentValue.Should().Be(0.5);
        result.Value.BaselineValue.Should().Be(0.0);

        _stateStoreMock.Verify(s => s.GetStateAsync(
            It.IsAny<DriftScope>(), It.IsAny<string>(), It.IsAny<DriftDimension>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScoreDimension_GetStateFails_PropagatesError()
    {
        // Arrange
        var baseline = CreateBaseline(mean: 0.8, sigma: 0.1);

        _stateStoreMock
            .Setup(s => s.GetStateAsync(DriftScope.Skill, "code_review", DriftDimension.Faithfulness, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result<EwmaState?>.Fail("Graph store unavailable"));

        var scorer = CreateScorer();

        // Act
        var result = await scorer.ScoreDimensionAsync(
            DriftDimension.Faithfulness, 0.75, baseline, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("Graph store unavailable");

        _stateStoreMock.Verify(s => s.SaveStateAsync(
            It.IsAny<EwmaState>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScoreDimension_SaveStateFails_PropagatesError()
    {
        // Arrange
        var baseline = CreateBaseline(mean: 0.8, sigma: 0.1);

        _stateStoreMock
            .Setup(s => s.GetStateAsync(DriftScope.Skill, "code_review", DriftDimension.Faithfulness, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result<EwmaState?>.Success(null));

        _stateStoreMock
            .Setup(s => s.SaveStateAsync(It.IsAny<EwmaState>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.Common.Result.Fail("Write failed"));

        var scorer = CreateScorer();

        // Act
        var result = await scorer.ScoreDimensionAsync(
            DriftDimension.Faithfulness, 0.75, baseline, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("Write failed");
    }
}

using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using FluentAssertions;
using Infrastructure.AI.DriftDetection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.DriftDetection;

public sealed class CompositeDriftNotifierTests
{
    private readonly Mock<ILogger<CompositeDriftNotifier>> _loggerMock = new();

    private static DriftScore CreateTestScore() => new()
    {
        ScoreId = Guid.NewGuid(),
        BaselineId = Guid.NewGuid(),
        Scope = DriftScope.Skill,
        ScopeIdentifier = "code_review",
        Dimensions = new Dictionary<DriftDimension, DriftDimensionScore>
        {
            [DriftDimension.Faithfulness] = new()
            {
                CurrentValue = 0.7,
                BaselineValue = 0.8,
                EwmaValue = 0.75,
                Deviation = 0.5
            }
        }.AsReadOnly(),
        OverallDrift = 0.5,
        Severity = DriftSeverity.None,
        ScoredAt = DateTimeOffset.UtcNow
    };

    private static DriftEvent CreateTestEvent() => new()
    {
        EventId = Guid.NewGuid(),
        DriftScore = CreateTestScore(),
        DetectedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task NotifyDriftDetected_FansOutToAllChannels()
    {
        // Arrange
        var channel1 = new Mock<IDriftNotificationChannel>();
        var channel2 = new Mock<IDriftNotificationChannel>();
        var channel3 = new Mock<IDriftNotificationChannel>();
        var notifier = new CompositeDriftNotifier(
            [channel1.Object, channel2.Object, channel3.Object], _loggerMock.Object);
        var score = CreateTestScore();

        // Act
        await notifier.NotifyDriftDetectedAsync(score, CancellationToken.None);

        // Assert
        channel1.Verify(c => c.NotifyDriftDetectedAsync(score, It.IsAny<CancellationToken>()), Times.Once);
        channel2.Verify(c => c.NotifyDriftDetectedAsync(score, It.IsAny<CancellationToken>()), Times.Once);
        channel3.Verify(c => c.NotifyDriftDetectedAsync(score, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyDriftResolved_FansOutToAllChannels()
    {
        // Arrange
        var channel1 = new Mock<IDriftNotificationChannel>();
        var channel2 = new Mock<IDriftNotificationChannel>();
        var notifier = new CompositeDriftNotifier(
            [channel1.Object, channel2.Object], _loggerMock.Object);
        var driftEvent = CreateTestEvent();

        // Act
        await notifier.NotifyDriftResolvedAsync(driftEvent, CancellationToken.None);

        // Assert
        channel1.Verify(c => c.NotifyDriftResolvedAsync(driftEvent, It.IsAny<CancellationToken>()), Times.Once);
        channel2.Verify(c => c.NotifyDriftResolvedAsync(driftEvent, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChannelFailure_LogsWarning_DoesNotBlockOtherChannels()
    {
        // Arrange
        var failingChannel = new Mock<IDriftNotificationChannel>();
        failingChannel
            .Setup(c => c.NotifyDriftDetectedAsync(It.IsAny<DriftScore>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Channel down"));

        var successChannel = new Mock<IDriftNotificationChannel>();
        var notifier = new CompositeDriftNotifier(
            [failingChannel.Object, successChannel.Object], _loggerMock.Object);

        // Act
        var act = () => notifier.NotifyDriftDetectedAsync(CreateTestScore(), CancellationToken.None);

        // Assert — should not throw
        await act.Should().NotThrowAsync();
        successChannel.Verify(c => c.NotifyDriftDetectedAsync(
            It.IsAny<DriftScore>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoChannels_CompletesWithoutError()
    {
        // Arrange
        var notifier = new CompositeDriftNotifier([], _loggerMock.Object);

        // Act
        var act = () => notifier.NotifyDriftDetectedAsync(CreateTestScore(), CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }
}

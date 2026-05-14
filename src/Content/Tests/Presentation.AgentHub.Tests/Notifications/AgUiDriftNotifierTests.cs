using Domain.AI.DriftDetection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Presentation.AgentHub.AgUi;
using Presentation.AgentHub.Notifications;
using Xunit;

namespace Presentation.AgentHub.Tests.Notifications;

/// <summary>
/// Tests for <see cref="AgUiDriftNotifier"/> -- verifies correct domain-to-AG-UI
/// event translation and writer invocation for drift detection events.
/// </summary>
public sealed class AgUiDriftNotifierTests
{
    private readonly Mock<IAgUiEventWriterAccessor> _accessorMock = new();
    private readonly Mock<IAgUiEventWriter> _writerMock = new();
    private readonly AgUiDriftNotifier _sut;

    public AgUiDriftNotifierTests()
    {
        _accessorMock.Setup(a => a.Writer).Returns(_writerMock.Object);
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _sut = new AgUiDriftNotifier(
            _accessorMock.Object,
            NullLogger<AgUiDriftNotifier>.Instance);
    }

    [Fact]
    public async Task NotifyDriftDetectedAsync_WarnSeverity_WritesDriftWarnEvent()
    {
        var score = CreateDriftScore(DriftSeverity.Warn);

        await _sut.NotifyDriftDetectedAsync(score, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<DriftWarnEvent>(e =>
                e.Scope == "Agent" &&
                e.ScopeIdentifier == "test-agent" &&
                e.MaxDeviation == 1.5 &&
                e.Severity == "Warn" &&
                e.Dimensions.Count == 2),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task NotifyDriftDetectedAsync_AlertSeverity_WritesDriftAlertEvent()
    {
        var score = CreateDriftScore(DriftSeverity.Alert);

        await _sut.NotifyDriftDetectedAsync(score, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<DriftAlertEvent>(e =>
                e.Scope == "Agent" &&
                e.ScopeIdentifier == "test-agent" &&
                e.BaselineId == score.BaselineId.ToString() &&
                e.Severity == "Alert"),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task NotifyDriftDetectedAsync_EscalateSeverity_WritesDriftEscalateEvent()
    {
        var score = CreateDriftScore(DriftSeverity.Escalate);

        await _sut.NotifyDriftDetectedAsync(score, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<DriftEscalateEvent>(e =>
                e.Scope == "Agent" &&
                e.ScopeIdentifier == "test-agent" &&
                e.BaselineId == score.BaselineId.ToString() &&
                e.EscalationId == string.Empty &&
                e.Severity == "Escalate"),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task NotifyDriftDetectedAsync_NoneSeverity_DoesNotWrite()
    {
        var score = CreateDriftScore(DriftSeverity.None);

        await _sut.NotifyDriftDetectedAsync(score, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyDriftResolvedAsync_WritesDriftResolvedEvent()
    {
        var resolvedAt = DateTimeOffset.UtcNow;
        var driftEvent = new DriftEvent
        {
            EventId = Guid.NewGuid(),
            DriftScore = CreateDriftScore(DriftSeverity.Alert),
            Resolution = new DriftResolution
            {
                ResolvedBy = DriftResolutionType.LearningApplied,
                ResolutionId = "learning-123",
                ResolvedAt = resolvedAt,
            },
            DetectedAt = resolvedAt.AddMinutes(-5),
        };

        await _sut.NotifyDriftResolvedAsync(driftEvent, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<DriftResolvedEvent>(e =>
                e.EventId == driftEvent.EventId.ToString() &&
                e.ResolutionType == "LearningApplied" &&
                e.ResolvedBy == "learning-123" &&
                e.ResolvedAt == resolvedAt),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task NotifyDriftResolvedAsync_NullResolution_DoesNotWrite()
    {
        var driftEvent = new DriftEvent
        {
            EventId = Guid.NewGuid(),
            DriftScore = CreateDriftScore(DriftSeverity.Alert),
            Resolution = null,
            DetectedAt = DateTimeOffset.UtcNow,
        };

        await _sut.NotifyDriftResolvedAsync(driftEvent, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyDriftDetectedAsync_NoWriter_SilentlyReturns()
    {
        _accessorMock.Setup(a => a.Writer).Returns((IAgUiEventWriter?)null);
        var sut = new AgUiDriftNotifier(
            _accessorMock.Object,
            NullLogger<AgUiDriftNotifier>.Instance);

        var score = CreateDriftScore(DriftSeverity.Warn);

        await sut.NotifyDriftDetectedAsync(score, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyDriftDetectedAsync_WriterThrows_CatchesAndDoesNotThrow()
    {
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Stream closed"));

        var score = CreateDriftScore(DriftSeverity.Warn);

        var act = () => _sut.NotifyDriftDetectedAsync(score, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NotifyDriftDetectedAsync_DimensionsMapCorrectly()
    {
        var score = CreateDriftScore(DriftSeverity.Warn);

        await _sut.NotifyDriftDetectedAsync(score, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<DriftWarnEvent>(e =>
                e.Dimensions.ContainsKey("Faithfulness") &&
                e.Dimensions.ContainsKey("Relevance") &&
                Math.Abs(e.Dimensions["Faithfulness"] - 1.5) < 0.001 &&
                Math.Abs(e.Dimensions["Relevance"] - 0.8) < 0.001),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task NotifyDriftDetectedAsync_OperationCanceledException_Propagates()
    {
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var score = CreateDriftScore(DriftSeverity.Warn);

        var act = () => _sut.NotifyDriftDetectedAsync(score, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task NotifyDriftResolvedAsync_NoWriter_SilentlyReturns()
    {
        _accessorMock.Setup(a => a.Writer).Returns((IAgUiEventWriter?)null);
        var sut = new AgUiDriftNotifier(
            _accessorMock.Object,
            NullLogger<AgUiDriftNotifier>.Instance);

        var driftEvent = new DriftEvent
        {
            EventId = Guid.NewGuid(),
            DriftScore = CreateDriftScore(DriftSeverity.Alert),
            Resolution = new DriftResolution
            {
                ResolvedBy = DriftResolutionType.ManualDismissal,
                ResolutionId = "op-1",
                ResolvedAt = DateTimeOffset.UtcNow,
            },
            DetectedAt = DateTimeOffset.UtcNow,
        };

        await sut.NotifyDriftResolvedAsync(driftEvent, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyDriftResolvedAsync_WriterThrows_CatchesAndDoesNotThrow()
    {
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Stream closed"));

        var driftEvent = new DriftEvent
        {
            EventId = Guid.NewGuid(),
            DriftScore = CreateDriftScore(DriftSeverity.Alert),
            Resolution = new DriftResolution
            {
                ResolvedBy = DriftResolutionType.BaselineAdjusted,
                ResolutionId = "baseline-456",
                ResolvedAt = DateTimeOffset.UtcNow,
            },
            DetectedAt = DateTimeOffset.UtcNow,
        };

        var act = () => _sut.NotifyDriftResolvedAsync(driftEvent, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private static DriftScore CreateDriftScore(DriftSeverity severity) => new()
    {
        ScoreId = Guid.NewGuid(),
        BaselineId = Guid.NewGuid(),
        Scope = DriftScope.Agent,
        ScopeIdentifier = "test-agent",
        Dimensions = new Dictionary<DriftDimension, DriftDimensionScore>
        {
            [DriftDimension.Faithfulness] = new()
            {
                CurrentValue = 0.75,
                BaselineValue = 0.90,
                EwmaValue = 0.78,
                Deviation = 1.5,
            },
            [DriftDimension.Relevance] = new()
            {
                CurrentValue = 0.85,
                BaselineValue = 0.92,
                EwmaValue = 0.87,
                Deviation = 0.8,
            },
        },
        OverallDrift = 1.5,
        Severity = severity,
        ScoredAt = DateTimeOffset.UtcNow,
    };
}

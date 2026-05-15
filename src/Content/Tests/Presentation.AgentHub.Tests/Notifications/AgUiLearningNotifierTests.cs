using Domain.AI.Learnings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Presentation.AgentHub.AgUi;
using Presentation.AgentHub.Notifications;
using Xunit;

namespace Presentation.AgentHub.Tests.Notifications;

/// <summary>
/// Tests for <see cref="AgUiLearningNotifier"/> -- verifies correct domain-to-AG-UI
/// event translation and writer invocation for learning lifecycle events.
/// </summary>
public sealed class AgUiLearningNotifierTests
{
    private readonly Mock<IAgUiEventWriterAccessor> _accessorMock = new();
    private readonly Mock<IAgUiEventWriter> _writerMock = new();
    private readonly AgUiLearningNotifier _sut;

    public AgUiLearningNotifierTests()
    {
        _accessorMock.Setup(a => a.Writer).Returns(_writerMock.Object);
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _sut = new AgUiLearningNotifier(
            _accessorMock.Object,
            NullLogger<AgUiLearningNotifier>.Instance);
    }

    [Fact]
    public async Task NotifyLearningCapturedAsync_EmitsLearningCapturedEvent()
    {
        var entry = CreateLearningEntry();

        await _sut.NotifyLearningCapturedAsync(entry, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<LearningCapturedEvent>(e =>
                e.LearningId == entry.LearningId.ToString() &&
                e.Category == "FactualCorrection" &&
                e.AgentId == "test-agent" &&
                e.TeamId == "test-team" &&
                e.IsGlobal == false &&
                e.SourceDescription == "User corrected date format"),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task NotifyLearningAppliedAsync_EmitsLearningAppliedEvent()
    {
        var entry = CreateLearningEntry();

        await _sut.NotifyLearningAppliedAsync(entry, "research-agent", CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<LearningAppliedEvent>(e =>
                e.LearningId == entry.LearningId.ToString() &&
                e.AgentId == "research-agent" &&
                e.Category == "FactualCorrection"),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task NotifyLearningCapturedAsync_NoActiveWriter_DoesNotWrite()
    {
        _accessorMock.Setup(a => a.Writer).Returns((IAgUiEventWriter?)null);
        var sut = new AgUiLearningNotifier(
            _accessorMock.Object,
            NullLogger<AgUiLearningNotifier>.Instance);

        var entry = CreateLearningEntry();

        await sut.NotifyLearningCapturedAsync(entry, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyLearningCapturedAsync_WriterThrows_CatchesAndDoesNotThrow()
    {
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Stream closed"));

        var entry = CreateLearningEntry();

        var act = () => _sut.NotifyLearningCapturedAsync(entry, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NotifyLearningCapturedAsync_OperationCanceledException_Propagates()
    {
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var entry = CreateLearningEntry();

        var act = () => _sut.NotifyLearningCapturedAsync(entry, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task NotifyLearningAppliedAsync_NoActiveWriter_DoesNotWrite()
    {
        _accessorMock.Setup(a => a.Writer).Returns((IAgUiEventWriter?)null);
        var sut = new AgUiLearningNotifier(
            _accessorMock.Object,
            NullLogger<AgUiLearningNotifier>.Instance);

        var entry = CreateLearningEntry();

        await sut.NotifyLearningAppliedAsync(entry, "agent-1", CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyLearningAppliedAsync_OperationCanceledException_Propagates()
    {
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var entry = CreateLearningEntry();

        var act = () => _sut.NotifyLearningAppliedAsync(entry, "agent-1", CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task NotifyLearningAppliedAsync_WriterThrows_CatchesAndDoesNotThrow()
    {
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Stream closed"));

        var entry = CreateLearningEntry();

        var act = () => _sut.NotifyLearningAppliedAsync(entry, "agent-1", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NotifyLearningCapturedAsync_GlobalScope_SetsIsGlobalTrue()
    {
        var entry = CreateLearningEntry() with
        {
            Scope = new LearningScope { AgentId = null, TeamId = null, IsGlobal = true },
        };

        await _sut.NotifyLearningCapturedAsync(entry, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<LearningCapturedEvent>(e =>
                e.AgentId == null &&
                e.TeamId == null &&
                e.IsGlobal == true),
            CancellationToken.None),
            Times.Once);
    }

    private static LearningEntry CreateLearningEntry() => new()
    {
        LearningId = Guid.NewGuid(),
        Category = LearningCategory.FactualCorrection,
        DecayClass = DecayClass.Permanent,
        Scope = new LearningScope { AgentId = "test-agent", TeamId = "test-team", IsGlobal = false },
        Content = "Always use ISO 8601 date formats",
        Source = new LearningSource
        {
            SourceType = LearningSourceType.HumanCorrection,
            SourceId = "correction-1",
            SourceDescription = "User corrected date format",
        },
        Provenance = new LearningProvenance
        {
            OriginPipeline = "chat",
            OriginTask = "date-formatting",
            OriginTimestamp = DateTimeOffset.UtcNow,
            Confidence = 0.95,
        },
        FeedbackWeight = 1.0,
        UpdateCount = 0,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}

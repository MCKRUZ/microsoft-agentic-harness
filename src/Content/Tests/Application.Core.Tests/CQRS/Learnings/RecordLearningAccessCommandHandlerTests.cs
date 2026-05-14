using Application.AI.Common.Interfaces.Learnings;
using Application.Core.CQRS.Learnings;
using Domain.AI.Learnings;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Tests for <see cref="RecordLearningAccessCommandHandler"/>.
/// </summary>
public sealed class RecordLearningAccessCommandHandlerTests
{
    private readonly Mock<ILearningsStore> _store = new();
    private readonly RecordLearningAccessCommandHandler _handler;

    public RecordLearningAccessCommandHandlerTests()
    {
        _handler = new RecordLearningAccessCommandHandler(
            _store.Object,
            NullLogger<RecordLearningAccessCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_UpdatesLastAccessedAt()
    {
        var learningId = Guid.NewGuid();
        var accessedAt = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        var learning = CreateLearning(learningId);

        _store.Setup(s => s.GetAsync(learningId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<LearningEntry?>.Success(learning));
        _store.Setup(s => s.UpdateAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await _handler.Handle(
            new RecordLearningAccessCommand { LearningIds = [learningId], AccessedAt = accessedAt },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _store.Verify(s => s.UpdateAsync(
            It.Is<LearningEntry>(e => e.LastAccessedAt == accessedAt),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_MissingLearning_SkipsWithoutError()
    {
        var learningId = Guid.NewGuid();
        _store.Setup(s => s.GetAsync(learningId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<LearningEntry?>.Success(null));

        var result = await _handler.Handle(
            new RecordLearningAccessCommand
            {
                LearningIds = [learningId],
                AccessedAt = DateTimeOffset.UtcNow
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _store.Verify(s => s.UpdateAsync(It.IsAny<LearningEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmptyIdList_ReturnsSuccess()
    {
        var result = await _handler.Handle(
            new RecordLearningAccessCommand
            {
                LearningIds = [],
                AccessedAt = DateTimeOffset.UtcNow
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _store.Verify(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static LearningEntry CreateLearning(Guid id) => new()
    {
        LearningId = id,
        Category = LearningCategory.FactualCorrection,
        DecayClass = DecayClass.Permanent,
        Scope = new LearningScope { IsGlobal = true },
        Content = "Test content",
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
        CreatedAt = DateTimeOffset.UtcNow
    };
}

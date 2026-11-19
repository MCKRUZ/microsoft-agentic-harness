using Application.AI.Common.Interfaces.KnowledgeGraph;
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
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Tests for <see cref="ForgetCommandHandler"/>.
/// </summary>
public sealed class ForgetCommandHandlerTests
{
    private readonly Mock<ILearningsStore> _store = new();

    [Fact]
    public async Task Handle_ValidId_SoftDeletesLearning()
    {
        var learning = SetupStored(new LearningScope { AgentId = "agent-1" });
        _store.Setup(s => s.SoftDeleteAsync(learning.LearningId, "outdated", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await CreateHandler().Handle(
            new ForgetCommand { LearningId = learning.LearningId, Reason = "outdated" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _store.Verify(s => s.SoftDeleteAsync(learning.LearningId, "outdated", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_NotFound_ReturnsNotFoundWithoutDeleting()
    {
        var learningId = Guid.NewGuid();
        _store.Setup(s => s.GetAsync(learningId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<LearningEntry?>.Success(null));

        var result = await CreateHandler().Handle(
            new ForgetCommand { LearningId = learningId, Reason = "cleanup" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
        _store.Verify(s => s.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// #968: a lesson scoped to one user cannot be deleted by another user, and the refusal looks
    /// like a missing learning so the id reveals nothing.
    /// </summary>
    [Theory]
    [InlineData("user-b")]
    [InlineData(null)]
    public async Task Handle_UserScopedLearning_OtherCaller_ReturnsNotFoundWithoutDeleting(string? caller)
    {
        var learning = SetupStored(new LearningScope { IsGlobal = true, UserId = "user-a" });

        var result = await CreateHandler(callerUserId: caller).Handle(
            new ForgetCommand { LearningId = learning.LearningId, Reason = "cleanup" },
            CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.NotFound);
        _store.Verify(s => s.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_UserScopedLearning_Owner_SoftDeletes()
    {
        var learning = SetupStored(new LearningScope { IsGlobal = true, UserId = "user-a" });
        _store.Setup(s => s.SoftDeleteAsync(learning.LearningId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await CreateHandler(callerUserId: "USER-A").Handle(
            new ForgetCommand { LearningId = learning.LearningId, Reason = "cleanup" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _store.Verify(s => s.SoftDeleteAsync(learning.LearningId, "cleanup", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_Disabled_ReturnsSuccessNoOp()
    {
        var handler = CreateHandler(new LearningsConfig { Enabled = false });

        var result = await handler.Handle(
            new ForgetCommand { LearningId = Guid.NewGuid(), Reason = "test" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _store.Verify(s => s.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private LearningEntry SetupStored(LearningScope scope)
    {
        var learning = new LearningEntry
        {
            LearningId = Guid.NewGuid(),
            Category = LearningCategory.FactualCorrection,
            DecayClass = DecayClass.Permanent,
            Scope = scope,
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
            CreatedAt = DateTimeOffset.UtcNow
        };
        _store.Setup(s => s.GetAsync(learning.LearningId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<LearningEntry?>.Success(learning));
        return learning;
    }

    private ForgetCommandHandler CreateHandler(LearningsConfig? config = null, string? callerUserId = null) => new(
        _store.Object,
        CreateOptions(config ?? new LearningsConfig()),
        NullLogger<ForgetCommandHandler>.Instance,
        Mock.Of<IKnowledgeScope>(k => k.UserId == callerUserId));

    private static IOptionsMonitor<AppConfig> CreateOptions(LearningsConfig config)
    {
        var appConfig = new AppConfig { AI = new AIConfig { Learnings = config } };
        var mock = new Mock<IOptionsMonitor<AppConfig>>();
        mock.Setup(m => m.CurrentValue).Returns(appConfig);
        return mock.Object;
    }
}

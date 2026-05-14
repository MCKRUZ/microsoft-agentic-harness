using Application.AI.Common.Interfaces.Learnings;
using Application.Core.CQRS.Learnings;
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
    private readonly ForgetCommandHandler _handler;

    public ForgetCommandHandlerTests()
    {
        _handler = CreateHandler();
    }

    [Fact]
    public async Task Handle_ValidId_SoftDeletesLearning()
    {
        var learningId = Guid.NewGuid();
        _store.Setup(s => s.SoftDeleteAsync(learningId, "outdated", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await _handler.Handle(
            new ForgetCommand { LearningId = learningId, Reason = "outdated" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _store.Verify(s => s.SoftDeleteAsync(learningId, "outdated", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_NotFound_ReturnsNotFound()
    {
        var learningId = Guid.NewGuid();
        _store.Setup(s => s.SoftDeleteAsync(learningId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.NotFound("Not found"));

        var result = await _handler.Handle(
            new ForgetCommand { LearningId = learningId, Reason = "cleanup" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
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

    private ForgetCommandHandler CreateHandler(LearningsConfig? config = null) => new(
        _store.Object,
        CreateOptions(config ?? new LearningsConfig()),
        NullLogger<ForgetCommandHandler>.Instance);

    private static IOptionsMonitor<AppConfig> CreateOptions(LearningsConfig config)
    {
        var appConfig = new AppConfig { AI = new AIConfig { Learnings = config } };
        var mock = new Mock<IOptionsMonitor<AppConfig>>();
        mock.Setup(m => m.CurrentValue).Returns(appConfig);
        return mock.Object;
    }
}

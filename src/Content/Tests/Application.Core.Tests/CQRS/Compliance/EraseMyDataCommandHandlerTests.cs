using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.Core.CQRS.Compliance.EraseMyData;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Compliance;

/// <summary>
/// Tests for <see cref="EraseMyDataCommandHandler"/> — proves the erasure is self-scoped to the ambient
/// caller, fails closed without an authenticated scope, and never leaks store internals on failure.
/// </summary>
public sealed class EraseMyDataCommandHandlerTests
{
    private readonly Mock<IKnowledgeScope> _scope = new();
    private readonly Mock<IErasureOrchestrator> _orchestrator = new();

    private EraseMyDataCommandHandler CreateHandler() => new(
        _scope.Object,
        _orchestrator.Object,
        NullLogger<EraseMyDataCommandHandler>.Instance);

    private static ErasureReceipt SampleReceipt(string scopeId) => new()
    {
        RequestId = "req-1",
        ScopeId = scopeId,
        RequestedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
        NodesDeleted = 3,
        EdgesDeleted = 5,
        FeedbackWeightsDeleted = 2,
        VectorEmbeddingsDeleted = 4
    };

    [Fact]
    public async Task Handle_AuthenticatedScope_ErasesAmbientOwnerAndReturnsReceipt()
    {
        _scope.SetupGet(s => s.UserId).Returns("user-1");
        var receipt = SampleReceipt("user-1");
        _orchestrator
            .Setup(o => o.EraseByOwnerAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(receipt);

        var result = await CreateHandler().Handle(new EraseMyDataCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(receipt);
        _orchestrator.Verify(o => o.EraseByOwnerAsync("user-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ErasesExactlyTheAmbientOwner_NeverAnyOtherOwner()
    {
        // The command carries no owner field; the only id ever passed to the orchestrator is the
        // ambient scope's user id. This proves a caller cannot direct the erasure at another owner.
        _scope.SetupGet(s => s.UserId).Returns("caller-oid");
        _orchestrator
            .Setup(o => o.EraseByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleReceipt("caller-oid"));

        await CreateHandler().Handle(new EraseMyDataCommand(), CancellationToken.None);

        _orchestrator.Verify(
            o => o.EraseByOwnerAsync(It.Is<string>(id => id == "caller-oid"), It.IsAny<CancellationToken>()),
            Times.Once);
        _orchestrator.Verify(
            o => o.EraseByOwnerAsync(It.Is<string>(id => id != "caller-oid"), It.IsAny<CancellationToken>()),
            Times.Never);
        _orchestrator.Verify(
            o => o.EraseByNodeIdsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_NoAuthenticatedScope_ReturnsForbiddenAndErasesNothing()
    {
        _scope.SetupGet(s => s.UserId).Returns((string?)null);

        var result = await CreateHandler().Handle(new EraseMyDataCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        _orchestrator.Verify(
            o => o.EraseByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_BlankScope_ReturnsForbiddenAndErasesNothing(string userId)
    {
        _scope.SetupGet(s => s.UserId).Returns(userId);

        var result = await CreateHandler().Handle(new EraseMyDataCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        _orchestrator.Verify(
            o => o.EraseByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_OrchestratorThrows_ReturnsScrubbedFailureWithoutLeakingDetail()
    {
        _scope.SetupGet(s => s.UserId).Returns("user-1");
        _orchestrator
            .Setup(o => o.EraseByOwnerAsync("user-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "Connection string Server=secret;Password=hunter2 failed at C:\\internal\\path"));

        var result = await CreateHandler().Handle(new EraseMyDataCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.General);
        string.Join(" ", result.Errors).Should().NotContain("hunter2");
        string.Join(" ", result.Errors).Should().NotContain("C:\\internal");
    }
}

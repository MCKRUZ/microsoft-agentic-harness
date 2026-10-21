using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.Core.Compliance;
using Application.Core.CQRS.Compliance.EraseMyData;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using Domain.Common.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Compliance;

/// <summary>
/// Tests for <see cref="EraseMyDataCommandHandler"/> — proves the erasure is self-scoped to the ambient
/// caller, fails closed and audited without an authenticated scope, and never leaks store internals on failure.
/// </summary>
public sealed class EraseMyDataCommandHandlerTests
{
    private readonly Mock<IKnowledgeScope> _scope = new();
    private readonly Mock<IPersonalDataErasure> _orchestrator = new();
    private readonly List<AuditEntry> _records = [];

    private EraseMyDataCommandHandler CreateHandler()
    {
        var sink = new Mock<IAuditSink>();
        sink.Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEntry, CancellationToken>((e, _) => _records.Add(e))
            .Returns(ValueTask.CompletedTask);
        var erasure = new AuditedErasure(
            _orchestrator.Object, Mock.Of<IErasureSubjectReference>(), sink.Object, TimeProvider.System,
            NullLogger<AuditedErasure>.Instance);
        return new EraseMyDataCommandHandler(_scope.Object, erasure, NullLogger<EraseMyDataCommandHandler>.Instance);
    }

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
            .Setup(o => o.EraseOwnerAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(receipt);

        var result = await CreateHandler().Handle(new EraseMyDataCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(receipt);
        _orchestrator.Verify(o => o.EraseOwnerAsync("user-1", It.IsAny<CancellationToken>()), Times.Once);
        _records.Should().OnlyContain(r => r.ExecutorId == ErasureActor.DataSubjectExecutor);
    }

    [Fact]
    public async Task Handle_ErasesExactlyTheAmbientOwner_NeverAnyOtherOwner()
    {
        // The command carries no owner field; the only id ever passed to the orchestrator is the
        // ambient scope's user id. This proves a caller cannot direct the erasure at another owner.
        _scope.SetupGet(s => s.UserId).Returns("caller-oid");
        _orchestrator
            .Setup(o => o.EraseOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleReceipt("caller-oid"));

        await CreateHandler().Handle(new EraseMyDataCommand(), CancellationToken.None);

        _orchestrator.Verify(
            o => o.EraseOwnerAsync(It.Is<string>(id => id == "caller-oid"), It.IsAny<CancellationToken>()),
            Times.Once);
        _orchestrator.Verify(
            o => o.EraseOwnerAsync(It.Is<string>(id => id != "caller-oid"), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_NoUsableScope_ReturnsForbiddenRecordsTheDenialAndErasesNothing(string? userId)
    {
        _scope.SetupGet(s => s.UserId).Returns(userId);

        var result = await CreateHandler().Handle(new EraseMyDataCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        _orchestrator.Verify(
            o => o.EraseOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _records.Should().ContainSingle().Which.Outcome.Should().Be(AuditOutcome.Denied);
    }

    [Fact]
    public async Task Handle_OrchestratorThrows_ReturnsScrubbedFailureWithoutLeakingDetail()
    {
        _scope.SetupGet(s => s.UserId).Returns("user-1");
        _orchestrator
            .Setup(o => o.EraseOwnerAsync("user-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "Connection string Server=secret;Password=hunter2 failed at C:\\internal\\path"));

        var result = await CreateHandler().Handle(new EraseMyDataCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.General);
        string.Join(" ", result.Errors).Should().NotContain("hunter2");
        string.Join(" ", result.Errors).Should().NotContain("C:\\internal");
    }
}

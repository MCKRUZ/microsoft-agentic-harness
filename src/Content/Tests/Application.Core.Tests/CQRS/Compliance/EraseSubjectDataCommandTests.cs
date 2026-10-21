using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Compliance;
using Application.Core.Compliance;
using Application.Core.CQRS.Compliance.EraseSubjectData;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Compliance;

/// <summary>
/// Operator-initiated erasure (#900): the named subject is erased exactly as given, the operator is recorded as the
/// actor, and an id that would miss stores or reach no one is refused before anything runs.
/// </summary>
public sealed class EraseSubjectDataCommandTests
{
    private readonly Mock<IPersonalDataErasure> _erasure = new();
    private readonly List<AuditEntry> _records = [];

    private EraseSubjectDataCommandHandler CreateHandler()
    {
        var sink = new Mock<IAuditSink>();
        sink.Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEntry, CancellationToken>((e, _) => _records.Add(e))
            .Returns(ValueTask.CompletedTask);
        return new EraseSubjectDataCommandHandler(new AuditedErasure(
            _erasure.Object, Mock.Of<IErasureSubjectReference>(), sink.Object, TimeProvider.System,
            NullLogger<AuditedErasure>.Instance));
    }

    private static ErasureReceipt Receipt() => new()
    {
        RequestId = "r",
        ScopeId = "s",
        RequestedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        NodesDeleted = 0,
        EdgesDeleted = 0,
        FeedbackWeightsDeleted = 0,
        VectorEmbeddingsDeleted = 0,
    };

    [Fact]
    public async Task Handle_ErasesTheNamedSubjectAsGivenAndRecordsTheOperator()
    {
        _erasure.Setup(e => e.EraseOwnerAsync("Subject-ABC", It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());

        var result = await CreateHandler().Handle(
            new EraseSubjectDataCommand { SubjectId = "Subject-ABC", OperatorId = "operator-1" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _erasure.Verify(e => e.EraseOwnerAsync("Subject-ABC", It.IsAny<CancellationToken>()), Times.Once);
        _records.Should().HaveCount(2).And.OnlyContain(r =>
            r.ExecutorId == "operator-1" && r.RequestType == nameof(EraseSubjectDataCommand));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" subject")]
    [InlineData("subject\t")]
    public void Validator_SubjectBlankOrPadded_IsRefused(string subjectId)
    {
        var result = new EraseSubjectDataCommandValidator().Validate(
            new EraseSubjectDataCommand { SubjectId = subjectId, OperatorId = "operator-1" });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(EraseSubjectDataCommand.SubjectId));
    }

    [Fact]
    public void Validator_SubjectTooLong_IsRefused()
    {
        var result = new EraseSubjectDataCommandValidator().Validate(new EraseSubjectDataCommand
        {
            SubjectId = new string('a', EraseSubjectDataCommandValidator.MaxSubjectIdLength + 1),
            OperatorId = "operator-1",
        });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validator_BlankOperator_IsRefused()
    {
        var result = new EraseSubjectDataCommandValidator().Validate(
            new EraseSubjectDataCommand { SubjectId = "subject", OperatorId = " " });

        result.Errors.Should().ContainSingle(e => e.PropertyName == nameof(EraseSubjectDataCommand.OperatorId));
    }

    [Fact]
    public void Validator_SubjectExactlyAsIssued_IsAccepted()
    {
        new EraseSubjectDataCommandValidator()
            .Validate(new EraseSubjectDataCommand { SubjectId = "8f2c-Mixed-Case", OperatorId = "operator-1" })
            .IsValid.Should().BeTrue();
    }
}

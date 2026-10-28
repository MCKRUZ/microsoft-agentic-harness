using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Compliance;
using Application.Core.Compliance;
using Application.Core.Tests.Compliance;
using Application.Core.CQRS.Compliance.ExportSubjectData;
using Domain.Common.Compliance;
using Domain.Common.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Compliance;

/// <summary>
/// An operator's copy of a named person's data (#902): the copy is made for the subject exactly as given, the operator is
/// recorded as the actor, and an id that would read as a person with no data is refused before anything runs.
/// </summary>
public sealed class ExportSubjectDataCommandTests
{
    private readonly Mock<IPersonalDataExport> _export = new();
    private readonly List<AuditEntry> _records = [];

    private ExportSubjectDataCommandHandler CreateHandler()
    {
        var sink = new Mock<IAuditSink>();
        sink.Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEntry, CancellationToken>((e, _) => _records.Add(e))
            .Returns(ValueTask.CompletedTask);
        return new ExportSubjectDataCommandHandler(new AuditedExport(
            _export.Object, Mock.Of<IErasureSubjectReference>(), sink.Object, new AcceptingComplianceAuditTrail(), TimeProvider.System,
            NullLogger<AuditedExport>.Instance));
    }

    [Fact]
    public async Task Handle_CopiesTheNamedSubjectAsGivenAndRecordsTheOperator()
    {
        _export.Setup(e => e.ExportOwnerAsync("Subject-ABC", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalDataExport("export-1", DateTimeOffset.UnixEpoch, [], []));

        var result = await CreateHandler().Handle(
            new ExportSubjectDataCommand { SubjectId = "Subject-ABC", OperatorId = "operator-1" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _export.Verify(e => e.ExportOwnerAsync("Subject-ABC", It.IsAny<CancellationToken>()), Times.Once);
        _records.Should().NotBeEmpty().And.OnlyContain(r =>
            r.ExecutorId == "operator-1" && r.RequestType == nameof(ExportSubjectDataCommand)
            && r.Action == AuditedExport.AuditAction);
        _records.SelectMany(r => r.Metadata?.Values ?? []).Should().NotContain(v => v.Contains("Subject-ABC"),
            "the person is named in the audit trail only by pseudonymous reference");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" subject")]
    [InlineData("subject\t")]
    public void Validator_SubjectBlankOrPadded_IsRefused(string subjectId)
    {
        var result = new ExportSubjectDataCommandValidator().Validate(
            new ExportSubjectDataCommand { SubjectId = subjectId, OperatorId = "operator-1" });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ExportSubjectDataCommand.SubjectId));
    }

    [Fact]
    public void Validator_SubjectTooLong_IsRefused()
    {
        var result = new ExportSubjectDataCommandValidator().Validate(new ExportSubjectDataCommand
        {
            SubjectId = new string('a', Application.Core.CQRS.Compliance.EraseSubjectData.EraseSubjectDataCommandValidator.MaxSubjectIdLength + 1),
            OperatorId = "operator-1",
        });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validator_BlankOperator_IsRefused()
    {
        var result = new ExportSubjectDataCommandValidator().Validate(
            new ExportSubjectDataCommand { SubjectId = "subject", OperatorId = " " });

        result.Errors.Should().ContainSingle(e => e.PropertyName == nameof(ExportSubjectDataCommand.OperatorId));
    }

    [Fact]
    public void Validator_SubjectExactlyAsIssued_IsAccepted()
    {
        new ExportSubjectDataCommandValidator()
            .Validate(new ExportSubjectDataCommand { SubjectId = "8f2c-Mixed-Case", OperatorId = "operator-1" })
            .IsValid.Should().BeTrue();
    }
}

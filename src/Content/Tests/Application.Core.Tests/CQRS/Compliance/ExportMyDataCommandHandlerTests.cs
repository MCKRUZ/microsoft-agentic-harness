using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.Core.Compliance;
using Application.Core.Tests.Compliance;
using Application.Core.CQRS.Compliance.ExportMyData;
using Domain.Common;
using Domain.Common.Compliance;
using Domain.Common.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Compliance;

/// <summary>
/// <see cref="ExportMyDataCommandHandler"/> (#902): the copy is always of the ambient caller, and without an authenticated
/// scope nothing is read and the refusal is recorded.
/// </summary>
public sealed class ExportMyDataCommandHandlerTests
{
    private readonly Mock<IKnowledgeScope> _scope = new();
    private readonly Mock<IPersonalDataExport> _export = new();
    private readonly List<AuditEntry> _records = [];

    private ExportMyDataCommandHandler CreateHandler()
    {
        var sink = new Mock<IAuditSink>();
        sink.Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEntry, CancellationToken>((e, _) => _records.Add(e))
            .Returns(ValueTask.CompletedTask);
        var audited = new AuditedExport(
            _export.Object, Mock.Of<IErasureSubjectReference>(), sink.Object, new AcceptingComplianceAuditTrail(), TimeProvider.System,
            NullLogger<AuditedExport>.Instance);
        return new ExportMyDataCommandHandler(_scope.Object, audited, NullLogger<ExportMyDataCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_AuthenticatedScope_CopiesTheCallersOwnData()
    {
        _scope.SetupGet(s => s.UserId).Returns("user-1");
        var copy = new PersonalDataExport("e-1", DateTimeOffset.UnixEpoch, [], []);
        _export.Setup(e => e.ExportOwnerAsync("user-1", It.IsAny<CancellationToken>())).ReturnsAsync(copy);

        var result = await CreateHandler().Handle(new ExportMyDataCommand(), CancellationToken.None);

        result.Value.Should().BeSameAs(copy);
        _export.Verify(e => e.ExportOwnerAsync(It.IsNotIn("user-1"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Handle_NoUserScope_IsForbiddenReadsNothingAndRecordsTheRefusal(string? userId)
    {
        _scope.SetupGet(s => s.UserId).Returns(userId);

        var result = await CreateHandler().Handle(new ExportMyDataCommand(), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        _export.VerifyNoOtherCalls();
        _records.Should().ContainSingle().Which.Outcome.Should().Be(AuditOutcome.Denied);
    }
}

using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Application.Core.Compliance;
using Application.Core.Tests.Compliance;
using Application.Core.CQRS.Compliance.RemoveDocument;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Compliance;

/// <summary>
/// Operator removal of a source document (#901): the named document is removed, the operator is recorded as the actor,
/// and an id that is not one ingestion could have formed is refused before anything runs.
/// </summary>
public sealed class RemoveDocumentCommandTests
{
    private const string DocumentId = "0123456789ABCDEF";

    private readonly Mock<IErasureOrchestrator> _orchestrator = new();
    private readonly List<AuditEntry> _records = [];

    private RemoveDocumentCommandHandler CreateHandler()
    {
        var sink = new Mock<IAuditSink>();
        sink.Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEntry, CancellationToken>((e, _) => _records.Add(e))
            .Returns(ValueTask.CompletedTask);
        return new RemoveDocumentCommandHandler(new AuditedDocumentRemoval(
            _orchestrator.Object, Mock.Of<IDocumentLoadLedger>(), sink.Object, new AcceptingComplianceAuditTrail(), TestHostEnvironment.Instance, TimeProvider.System, NullLogger<AuditedDocumentRemoval>.Instance));
    }

    [Fact]
    public async Task Handle_RemovesTheNamedDocumentAndRecordsTheOperator()
    {
        _orchestrator.Setup(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>())).ReturnsAsync(new ErasureReceipt
        {
            RequestId = "r",
            ScopeId = "document",
            RequestedAt = DateTimeOffset.UnixEpoch,
            CompletedAt = DateTimeOffset.UnixEpoch,
            NodesDeleted = 0,
            EdgesDeleted = 0,
            FeedbackWeightsDeleted = 0,
            VectorEmbeddingsDeleted = 0,
        });

        var result = await CreateHandler().Handle(
            new RemoveDocumentCommand { DocumentId = DocumentId, OperatorId = "operator-1" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _orchestrator.Verify(o => o.RemoveDocumentAsync(DocumentId, It.IsAny<CancellationToken>()), Times.Once);
        _records.Should().HaveCount(2).And.OnlyContain(r =>
            r.ExecutorId == "operator-1" && r.RequestType == nameof(RemoveDocumentCommand));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789ABCDE")]
    [InlineData("0123456789ABCDEF0")]
    [InlineData(" 0123456789ABCDEF")]
    [InlineData("doc_chunk_0")]
    public void Validator_NotADocumentId_IsRefused(string documentId)
    {
        var result = new RemoveDocumentCommandValidator().Validate(
            new RemoveDocumentCommand { DocumentId = documentId, OperatorId = "operator-1" });

        result.Errors.Should().ContainSingle(e => e.PropertyName == nameof(RemoveDocumentCommand.DocumentId));
    }

    [Fact]
    public void Validator_BlankOperator_IsRefused()
    {
        var result = new RemoveDocumentCommandValidator().Validate(
            new RemoveDocumentCommand { DocumentId = DocumentId, OperatorId = " " });

        result.Errors.Should().ContainSingle(e => e.PropertyName == nameof(RemoveDocumentCommand.OperatorId));
    }

    [Fact]
    public void Validator_DocumentIdAsIngestionFormsIt_IsAccepted() =>
        new RemoveDocumentCommandValidator()
            .Validate(new RemoveDocumentCommand { DocumentId = DocumentId, OperatorId = "operator-1" })
            .IsValid.Should().BeTrue();
}

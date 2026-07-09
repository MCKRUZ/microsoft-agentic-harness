using Application.AI.Common.Interfaces.RAG;
using Application.Core.CQRS.RAG.IngestDocument;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.RAG;

/// <summary>
/// Verifies that a partial ingest failure (one store write succeeds, the other throws)
/// compensates by deleting the already-written derived copies from BOTH stores, so no
/// un-erasable orphaned chunks/summaries remain for the failed document.
/// </summary>
public sealed class IngestDocumentCommandHandlerCompensationTests
{
    private const string DocumentId = "file:///docs/report.md";
    private const string CollectionName = "corpus-a";

    private readonly Mock<IDocumentParser> _parser = new();
    private readonly Mock<IStructureExtractor> _structureExtractor = new();
    private readonly Mock<IChunkingService> _chunker = new();
    private readonly Mock<IContextualEnricher> _enricher = new();
    private readonly Mock<IRaptorSummarizer> _raptor = new();
    private readonly Mock<IEmbeddingService> _embedding = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<IBm25Store> _bm25Store = new();

    public IngestDocumentCommandHandlerCompensationTests()
    {
        var chunks = new List<DocumentChunk>
        {
            new()
            {
                Id = $"{DocumentId}_chunk_0",
                DocumentId = DocumentId,
                SectionPath = "Root",
                Content = "chunk text",
                Tokens = 3,
                Metadata = new ChunkMetadata
                {
                    SourceUri = new Uri(DocumentId),
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                Embedding = new[] { 0.1f, 0.2f },
            },
        };

        _parser
            .Setup(p => p.ParseAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("# markdown");
        _structureExtractor
            .Setup(s => s.ExtractStructure(It.IsAny<string>()))
            .Returns(new SkeletonNode { Title = "Root", Level = 1, StartOffset = 0, EndOffset = 10 });
        _chunker
            .Setup(c => c.ChunkAsync(
                It.IsAny<string>(), It.IsAny<SkeletonNode>(), It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);
        _embedding
            .Setup(e => e.EmbedAsync(It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);
    }

    private IngestDocumentCommandHandler CreateHandler()
    {
        var appConfig = new AppConfig();
        appConfig.AI.Rag.Ingestion.EnableContextualEnrichment = false;
        appConfig.AI.Rag.Ingestion.EnableRaptorSummaries = false;

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(appConfig);

        return new IngestDocumentCommandHandler(
            _parser.Object,
            _structureExtractor.Object,
            _chunker.Object,
            _enricher.Object,
            _raptor.Object,
            _embedding.Object,
            _vectorStore.Object,
            _bm25Store.Object,
            NullLogger<IngestDocumentCommandHandler>.Instance,
            monitor.Object);
    }

    private static IngestDocumentCommand Command() => new()
    {
        DocumentUri = new Uri(DocumentId),
        CollectionName = CollectionName,
    };

    [Fact]
    public async Task Handle_Bm25WriteThrowsAfterVectorWriteSucceeds_CompensatesBothStores()
    {
        _vectorStore
            .Setup(v => v.IndexAsync(
                It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _bm25Store
            .Setup(b => b.IndexAsync(
                It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bm25 index unavailable"));

        var result = await CreateHandler().Handle(Command(), CancellationToken.None);

        result.Success.Should().BeFalse();

        _vectorStore.Verify(
            v => v.DeleteAsync(DocumentId, CollectionName, It.IsAny<CancellationToken>()),
            Times.Once,
            "the vector store wrote its chunks and must be rolled back on partial failure");
        _bm25Store.Verify(
            b => b.DeleteAsync(DocumentId, CollectionName, It.IsAny<CancellationToken>()),
            Times.Once,
            "the BM25 delete is document-scoped and idempotent — safe even if nothing was written");
    }

    [Fact]
    public async Task Handle_VectorWriteThrowsAfterBm25WriteSucceeds_CompensatesBothStores()
    {
        _bm25Store
            .Setup(b => b.IndexAsync(
                It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _vectorStore
            .Setup(v => v.IndexAsync(
                It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("vector index unavailable"));

        var result = await CreateHandler().Handle(Command(), CancellationToken.None);

        result.Success.Should().BeFalse();

        _vectorStore.Verify(
            v => v.DeleteAsync(DocumentId, CollectionName, It.IsAny<CancellationToken>()),
            Times.Once);
        _bm25Store.Verify(
            b => b.DeleteAsync(DocumentId, CollectionName, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_CompensatingDeleteItselfFails_StillReturnsOriginalFailureWithoutThrowing()
    {
        _vectorStore
            .Setup(v => v.IndexAsync(
                It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _bm25Store
            .Setup(b => b.IndexAsync(
                It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bm25 index unavailable"));
        _vectorStore
            .Setup(v => v.DeleteAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("delete also failed"));

        var act = async () => await CreateHandler().Handle(Command(), CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Which.Success.Should().BeFalse();
    }
}

using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Application.Core.CQRS.RAG.IngestDocument;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.RAG;

/// <summary>
/// The ingest records each document's load time before any store is written (#901), so the optional document age limit
/// can reach every document the corpus holds: one stored without a load time could never age out.
/// </summary>
public sealed class IngestDocumentCommandHandlerLoadLedgerTests
{
    private const string WellFormedId = "0123456789ABCDEF";

    private readonly Mock<IDocumentParser> _parser = new();
    private readonly Mock<IStructureExtractor> _structureExtractor = new();
    private readonly Mock<IChunkingService> _chunker = new();
    private readonly Mock<IEmbeddingService> _embedding = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<IBm25Store> _bm25Store = new();
    private readonly Mock<IDocumentLoadLedger> _ledger = new();
    private readonly List<string> _calls = [];

    private void Arrange(string documentId)
    {
        var chunks = new List<DocumentChunk>
        {
            new()
            {
                Id = $"{documentId}_chunk_0",
                DocumentId = documentId,
                SectionPath = "Root",
                Content = "chunk text",
                Tokens = 3,
                Metadata = new ChunkMetadata { SourceUri = new Uri("file:///docs/report.md"), CreatedAt = DateTimeOffset.UtcNow },
                Embedding = new[] { 0.1f, 0.2f },
            },
        };

        _parser.Setup(p => p.ParseAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>())).ReturnsAsync("# markdown");
        _structureExtractor.Setup(s => s.ExtractStructure(It.IsAny<string>()))
            .Returns(new SkeletonNode { Title = "Root", Level = 1, StartOffset = 0, EndOffset = 10 });
        _chunker.Setup(c => c.ChunkAsync(
                It.IsAny<string>(), It.IsAny<SkeletonNode>(), It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);
        _embedding.Setup(e => e.EmbedAsync(It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);
        _vectorStore.Setup(v => v.IndexAsync(It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("vector"))
            .Returns(Task.CompletedTask);
        _bm25Store.Setup(b => b.IndexAsync(It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("bm25"))
            .Returns(Task.CompletedTask);
        _ledger.Setup(l => l.RecordLoadedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("ledger"))
            .Returns(Task.CompletedTask);
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
            new Mock<IContextualEnricher>().Object,
            new Mock<IRaptorSummarizer>().Object,
            _embedding.Object,
            _vectorStore.Object,
            _bm25Store.Object,
            new Mock<IKnowledgeScope>().Object,
            _ledger.Object,
            NullLogger<IngestDocumentCommandHandler>.Instance,
            monitor.Object,
            new ServiceCollection().BuildServiceProvider());
    }

    private static IngestDocumentCommand Command() => new() { DocumentUri = new Uri("file:///docs/report.md") };

    [Fact]
    public async Task Handle_RecordsTheLoadBeforeAnyStoreIsWritten()
    {
        Arrange(WellFormedId);

        var result = await CreateHandler().Handle(Command(), CancellationToken.None);

        result.Success.Should().BeTrue();
        _ledger.Verify(l => l.RecordLoadedAsync(WellFormedId, It.IsAny<CancellationToken>()), Times.Once);
        _calls.First().Should().Be("ledger", "a document stored before its load is recorded could be left with no load time");
    }

    [Fact]
    public async Task Handle_LoadCannotBeRecorded_FailsWithoutWritingAnyStore()
    {
        Arrange(WellFormedId);
        _ledger.Setup(l => l.RecordLoadedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database locked"));

        var result = await CreateHandler().Handle(Command(), CancellationToken.None);

        result.Success.Should().BeFalse("a document stored with no load time could never age out");
        _calls.Should().NotContain(["vector", "bm25"]);

        // Nothing of this ingest was written, so nothing is compensated: deleting by document id would remove the copy a
        // previous load left in place.
        _vectorStore.Verify(v => v.DeleteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _bm25Store.Verify(b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_IdNoChunkerForms_IsStoredButNotRecorded()
    {
        // A custom chunker's id cannot be removed by id at all, so recording it would only make the age limit fail on it
        // every sweep; the ingest itself still succeeds.
        Arrange("custom-chunker-id");

        var result = await CreateHandler().Handle(Command(), CancellationToken.None);

        result.Success.Should().BeTrue();
        _ledger.Verify(l => l.RecordLoadedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _calls.Should().Contain(["vector", "bm25"]);
    }
}

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
/// Verifies the K4 behaviors of <see cref="IngestDocumentCommandHandler"/>:
/// owner/tenant provenance stamping from the ambient <see cref="IKnowledgeScope"/>
/// (always on, null without identity, never a filter), and opt-in
/// <c>ScopedCollections</c> resolution (derived from the tenant when on, caller
/// pass-through when off, compensation targeting the same derived collection).
/// </summary>
public sealed class IngestDocumentCommandHandlerProvenanceTests
{
    private const string DocumentId = "file:///docs/report.md";

    private readonly Mock<IDocumentParser> _parser = new();
    private readonly Mock<IStructureExtractor> _structureExtractor = new();
    private readonly Mock<IChunkingService> _chunker = new();
    private readonly Mock<IContextualEnricher> _enricher = new();
    private readonly Mock<IRaptorSummarizer> _raptor = new();
    private readonly Mock<IEmbeddingService> _embedding = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<IBm25Store> _bm25Store = new();
    private readonly Mock<IKnowledgeScope> _scope = new();

    public IngestDocumentCommandHandlerProvenanceTests()
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
            .ReturnsAsync((IReadOnlyList<DocumentChunk> c, CancellationToken _) => c);
    }

    private IngestDocumentCommandHandler CreateHandler(Action<AppConfig>? configure = null)
    {
        var appConfig = new AppConfig();
        appConfig.AI.Rag.Ingestion.EnableContextualEnrichment = false;
        appConfig.AI.Rag.Ingestion.EnableRaptorSummaries = false;
        configure?.Invoke(appConfig);

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
            _scope.Object,
            NullLogger<IngestDocumentCommandHandler>.Instance,
            monitor.Object,
            new ServiceCollection().BuildServiceProvider());
    }

    private static IngestDocumentCommand Command(string? collectionName = null) => new()
    {
        DocumentUri = new Uri(DocumentId),
        CollectionName = collectionName,
    };

    private IReadOnlyList<DocumentChunk> CapturedVectorChunks()
    {
        var invocation = _vectorStore.Invocations
            .Single(i => i.Method.Name == nameof(IVectorStore.IndexAsync));
        return (IReadOnlyList<DocumentChunk>)invocation.Arguments[0];
    }

    [Fact]
    public async Task Handle_AmbientIdentityPresent_StampsOwnerAndTenantOnIndexedChunks()
    {
        _scope.SetupGet(s => s.UserId).Returns("user-1");
        _scope.SetupGet(s => s.TenantId).Returns("tenant-a");

        var result = await CreateHandler().Handle(Command(), CancellationToken.None);

        result.Success.Should().BeTrue();
        var chunks = CapturedVectorChunks();
        chunks.Should().AllSatisfy(c =>
        {
            c.Metadata.OwnerId.Should().Be("user-1");
            c.Metadata.TenantId.Should().Be("tenant-a");
        });
    }

    [Fact]
    public async Task Handle_NoAmbientIdentity_LeavesStampsNull()
    {
        var result = await CreateHandler().Handle(Command(), CancellationToken.None);

        result.Success.Should().BeTrue();
        var chunks = CapturedVectorChunks();
        chunks.Should().AllSatisfy(c =>
        {
            c.Metadata.OwnerId.Should().BeNull();
            c.Metadata.TenantId.Should().BeNull();
        });
    }

    [Fact]
    public async Task Handle_ScopedCollectionsEnabled_DerivesCollectionFromAmbientTenant()
    {
        _scope.SetupGet(s => s.TenantId).Returns("tenant-a");
        var expected = ScopedCollectionName.DeriveForTenant("tenant-a");
        var handler = CreateHandler(cfg => cfg.AI.Rag.ScopedCollections.Enabled = true);

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Success.Should().BeTrue();
        _vectorStore.Verify(v => v.IndexAsync(
            It.IsAny<IReadOnlyList<DocumentChunk>>(), expected, It.IsAny<CancellationToken>()), Times.Once);
        _bm25Store.Verify(b => b.IndexAsync(
            It.IsAny<IReadOnlyList<DocumentChunk>>(), expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ScopedCollectionsEnabledWithoutTenant_UsesGlobalDefaultCollection()
    {
        var handler = CreateHandler(cfg => cfg.AI.Rag.ScopedCollections.Enabled = true);

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Success.Should().BeTrue();
        _vectorStore.Verify(v => v.IndexAsync(
            It.IsAny<IReadOnlyList<DocumentChunk>>(), null, It.IsAny<CancellationToken>()), Times.Once);
        _bm25Store.Verify(b => b.IndexAsync(
            It.IsAny<IReadOnlyList<DocumentChunk>>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ScopedCollectionsDisabled_PassesCallerSuppliedCollectionUnchanged()
    {
        _scope.SetupGet(s => s.TenantId).Returns("tenant-a");

        var result = await CreateHandler().Handle(Command("corpus-a"), CancellationToken.None);

        result.Success.Should().BeTrue();
        _vectorStore.Verify(v => v.IndexAsync(
            It.IsAny<IReadOnlyList<DocumentChunk>>(), "corpus-a", It.IsAny<CancellationToken>()), Times.Once);
        _bm25Store.Verify(b => b.IndexAsync(
            It.IsAny<IReadOnlyList<DocumentChunk>>(), "corpus-a", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ScopedCollectionsEnabledAndStoreWriteFails_CompensatesInDerivedCollection()
    {
        _scope.SetupGet(s => s.TenantId).Returns("tenant-a");
        var expected = ScopedCollectionName.DeriveForTenant("tenant-a");
        _bm25Store
            .Setup(b => b.IndexAsync(
                It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bm25 down"));
        var handler = CreateHandler(cfg => cfg.AI.Rag.ScopedCollections.Enabled = true);

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Success.Should().BeFalse();
        _vectorStore.Verify(v => v.DeleteAsync(
            DocumentId, expected, It.IsAny<CancellationToken>()), Times.Once,
            "compensation must delete from the same derived collection the index writes targeted");
        _bm25Store.Verify(b => b.DeleteAsync(
            DocumentId, expected, It.IsAny<CancellationToken>()), Times.Once);
    }
}

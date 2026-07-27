using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Compliance;
using Infrastructure.AI.RAG.Retrieval;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Compliance;

/// <summary>
/// End-to-end proof that the right-to-erasure cascade reaches per-tenant scoped
/// collections: chunks ingested into a tenant-derived collection through the REAL local
/// stores (FAISS + SQLite FTS5) are gone from BOTH stores after
/// <see cref="DefaultErasureOrchestrator.EraseByOwnerAsync"/>, the receipt counts match
/// the rows actually removed, and a document the graph manifest points at that matches
/// nothing surfaces on <see cref="ErasureReceipt.UnmatchedDocumentIds"/> instead of
/// silently counting as purged.
/// </summary>
public sealed class ErasureScopedCollectionsIntegrationTests
{
    private const string Owner = "user-1";
    private const string DocumentId = "doc-erase";
    private const string ChunkId = $"{DocumentId}_chunk_0";

    private static readonly float[] Embedding = [0.5f, 0.5f, 0.5f];
    private static readonly string TenantCollection =
        ScopedCollectionName.DeriveForTenant("tenant-a")!;

    private readonly Mock<IKnowledgeGraphStore> _graphStore = new();
    private readonly Mock<IFeedbackStore> _feedbackStore = new();
    private readonly FaissVectorStore _vectorStore = new(NullLogger<FaissVectorStore>.Instance);
    private readonly SqliteFts5Store _bm25Store = new(
        $"Data Source=ErasureScoped-{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
        NullLogger<SqliteFts5Store>.Instance);

    public ErasureScopedCollectionsIntegrationTests()
    {
        _graphStore.Setup(g => g.DeleteNodesAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> ids, CancellationToken _) =>
                new NodeDeletionResult { DeletedNodeIds = ids.ToList(), DeletedEdgeIds = [] });
        _graphStore.Setup(g => g.DeleteEdgesByOwnerAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private DefaultErasureOrchestrator CreateOrchestrator() => new(
        _graphStore.Object,
        _feedbackStore.Object,
        _vectorStore,
        Mock.Of<IMemoryAuditSink>(),
        TimeProvider.System,
        NullLogger<DefaultErasureOrchestrator>.Instance,
        _bm25Store);

    private void SetupOwnerNode(string chunkId)
    {
        _graphStore.Setup(g => g.GetNodesByOwnerAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new GraphNode
                {
                    Id = "n1", Name = "T", Type = "Fact", OwnerId = Owner,
                    ChunkIds = [chunkId],
                },
            ]);
    }

    private static DocumentChunk StampedChunk() => new()
    {
        Id = ChunkId,
        DocumentId = DocumentId,
        SectionPath = "Root",
        Content = "erasable tenant scoped content",
        Tokens = 4,
        Embedding = Embedding,
        Metadata = new ChunkMetadata
        {
            SourceUri = new Uri($"file:///docs/{DocumentId}.md"),
            CreatedAt = DateTimeOffset.UtcNow,
            OwnerId = Owner,
            TenantId = "tenant-a",
        },
    };

    [Fact]
    public async Task EraseByOwner_ChunksInTenantDerivedCollection_RemovedFromBothStoresWithMatchingCounts()
    {
        await _vectorStore.IndexAsync([StampedChunk()], TenantCollection);
        await _bm25Store.IndexAsync([StampedChunk()], TenantCollection);
        SetupOwnerNode(ChunkId);

        var receipt = await CreateOrchestrator().EraseByOwnerAsync(Owner);

        receipt.VectorEmbeddingsDeleted.Should().Be(1,
            "the tenant-derived collection must be reached — a default-collection delete would remove 0");
        receipt.Bm25DocumentsDeleted.Should().Be(1);
        receipt.UnmatchedDocumentIds.Should().BeEmpty();

        (await _vectorStore.SearchAsync(Embedding, topK: 5, TenantCollection))
            .Should().BeEmpty("the embeddings must be gone from the tenant collection");
        (await _bm25Store.SearchAsync("erasable tenant", topK: 5, TenantCollection))
            .Should().BeEmpty("the BM25 rows must be gone from the tenant collection");
    }

    [Fact]
    public async Task EraseByOwner_ManifestPointsAtDocumentWithNoRows_ReceiptDoesNotOverCount()
    {
        // Nothing ingested: the graph manifest references chunks whose derived content was
        // never (or no longer) in the stores. The receipt must report 0 — never the
        // submitted document count — and surface the unmatched document as detail.
        SetupOwnerNode("ghost-doc_chunk_0");

        var receipt = await CreateOrchestrator().EraseByOwnerAsync(Owner);

        receipt.VectorEmbeddingsDeleted.Should().Be(0);
        receipt.Bm25DocumentsDeleted.Should().Be(0);
        receipt.UnmatchedDocumentIds.Should().ContainSingle().Which.Should().Be("ghost-doc");
    }
}

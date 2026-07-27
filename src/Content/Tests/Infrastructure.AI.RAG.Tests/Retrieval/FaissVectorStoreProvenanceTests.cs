using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.Retrieval;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Retrieval;

/// <summary>
/// Verifies that <see cref="FaissVectorStore"/> keeps the owner/tenant provenance stamps
/// on the stored record but never exposes them on search results (matching the read-path
/// projection of the persistent stores), and that tenant-derived collections are isolated
/// from each other on the dense side of the local hybrid pairing.
/// </summary>
public sealed class FaissVectorStoreProvenanceTests
{
    private static readonly float[] Embedding = [0.5f, 0.5f, 0.5f];

    private static DocumentChunk StampedChunk(string id, string? ownerId, string? tenantId)
    {
        var chunk = RagTestData.CreateChunk(id: id, documentId: $"doc-{id}");
        return chunk with
        {
            Embedding = Embedding,
            Metadata = chunk.Metadata with { OwnerId = ownerId, TenantId = tenantId },
        };
    }

    [Fact]
    public async Task SearchAsync_StampedChunk_DoesNotExposeStampsOnResults()
    {
        var store = new FaissVectorStore(NullLogger<FaissVectorStore>.Instance);
        await store.IndexAsync([StampedChunk("s1", "user-1", "tenant-a")]);

        var results = await store.SearchAsync(Embedding, topK: 5);

        var metadata = results.Should().ContainSingle().Which.Chunk.Metadata;
        metadata.OwnerId.Should().BeNull(
            "stamps are erasure data; exposing them would tell every searcher who ingested each chunk");
        metadata.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_TenantDerivedCollections_AreIsolated()
    {
        var store = new FaissVectorStore(NullLogger<FaissVectorStore>.Instance);
        await store.IndexAsync(
            [StampedChunk("a1", "u1", "tenant-a")], ScopedCollectionName.DeriveForTenant("tenant-a"));
        await store.IndexAsync(
            [StampedChunk("b1", "u2", "tenant-b")], ScopedCollectionName.DeriveForTenant("tenant-b"));

        var seenByB = await store.SearchAsync(
            Embedding, topK: 5, ScopedCollectionName.DeriveForTenant("tenant-b"));

        seenByB.Should().ContainSingle().Which.Chunk.Id.Should().Be("b1");
    }
}

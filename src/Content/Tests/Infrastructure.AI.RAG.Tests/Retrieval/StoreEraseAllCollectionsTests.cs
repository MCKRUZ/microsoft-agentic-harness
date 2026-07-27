using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.Retrieval;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Retrieval;

/// <summary>
/// Verifies the erasure-delete contract of the local stores: <c>DeleteAsync</c> returns
/// the rows it actually removed (never the requested count), and
/// <c>DeleteFromAllCollectionsAsync</c> reaches a document's chunks in EVERY collection —
/// including named collections a default-scoped delete would silently miss, which is both
/// the ScopedCollections erasure path and the flag-off named-collection regression.
/// </summary>
public sealed class StoreEraseAllCollectionsTests
{
    private static readonly float[] Embedding = [0.4f, 0.4f, 0.4f];

    private static SqliteFts5Store CreateFtsStore() => new(
        $"Data Source=EraseAll-{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
        NullLogger<SqliteFts5Store>.Instance);

    private static DocumentChunk Chunk(string id, string documentId)
    {
        var chunk = RagTestData.CreateChunk(
            id: id, content: "erasable content body", documentId: documentId);
        return chunk with { Embedding = Embedding };
    }

    [Fact]
    public async Task DeleteFromAllCollectionsAsync_Sqlite_RemovesDocumentFromNamedCollection()
    {
        // BLOCKING-2 regression: flag OFF, caller ingested into a named collection; the
        // erasure delete must still remove the rows (the collection-scoped DeleteAsync
        // deliberately would not).
        var store = CreateFtsStore();
        await store.IndexAsync([Chunk("c1", "doc-1")], "docs");

        var deleted = await store.DeleteFromAllCollectionsAsync("doc-1");

        deleted.Should().Be(1);
        (await store.SearchAsync("erasable content", topK: 5, "docs")).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFromAllCollectionsAsync_Sqlite_SpansMultipleCollections()
    {
        var store = CreateFtsStore();
        await store.IndexAsync([Chunk("c1", "doc-1")], "coll-a");
        await store.IndexAsync([Chunk("c2", "doc-1")], "coll-b");
        await store.IndexAsync([Chunk("c3", "doc-other")], "coll-a");

        var deleted = await store.DeleteFromAllCollectionsAsync("doc-1");

        deleted.Should().Be(2, "the document's rows in every collection count, others do not");
        (await store.SearchAsync("erasable content", topK: 5, "coll-a"))
            .Should().ContainSingle().Which.Chunk.Id.Should().Be("c3");
    }

    [Fact]
    public async Task DeleteAsync_Sqlite_ReturnsActualRemovedCount()
    {
        var store = CreateFtsStore();
        await store.IndexAsync([Chunk("c1", "doc-1"), Chunk("c2", "doc-1")]);

        (await store.DeleteAsync("doc-1")).Should().Be(2);
        (await store.DeleteAsync("doc-1")).Should().Be(0, "a repeat delete removes nothing");
    }

    [Fact]
    public async Task DeleteFromAllCollectionsAsync_Faiss_SpansMultipleCollections()
    {
        var store = new FaissVectorStore(NullLogger<FaissVectorStore>.Instance);
        await store.IndexAsync([Chunk("c1", "doc-1")], "coll-a");
        await store.IndexAsync([Chunk("c2", "doc-1")], "coll-b");
        await store.IndexAsync([Chunk("c3", "doc-other")], "coll-a");

        var deleted = await store.DeleteFromAllCollectionsAsync("doc-1");

        deleted.Should().Be(2);
        (await store.SearchAsync(Embedding, topK: 5, "coll-a"))
            .Should().ContainSingle().Which.Chunk.Id.Should().Be("c3");
        (await store.SearchAsync(Embedding, topK: 5, "coll-b")).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_Faiss_ScopedToOneCollection_ReturnsActualCount()
    {
        var store = new FaissVectorStore(NullLogger<FaissVectorStore>.Instance);
        await store.IndexAsync([Chunk("c1", "doc-1")], "coll-a");
        await store.IndexAsync([Chunk("c2", "doc-1")], "coll-b");

        (await store.DeleteAsync("doc-1", "coll-a")).Should().Be(1);
        (await store.SearchAsync(Embedding, topK: 5, "coll-b"))
            .Should().ContainSingle("the other collection's copy survives a collection-scoped delete");
    }
}

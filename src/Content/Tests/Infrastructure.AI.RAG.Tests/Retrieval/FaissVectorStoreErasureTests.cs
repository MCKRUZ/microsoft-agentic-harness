using Domain.AI.RAG.Models;
using Domain.Common.Compliance;
using FluentAssertions;
using Infrastructure.AI.RAG.Retrieval;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Retrieval;

/// <summary>
/// Right-to-erasure (#900) for <see cref="FaissVectorStore"/>: every chunk stamped with the person's owner id is removed
/// from every collection, and every other chunk — another owner's, an unstamped one — stays searchable.
/// </summary>
/// <remarks>
/// Over-deletion destroys another person's data; under-deletion leaves the person's data behind while the erasure
/// receipt claims success. Ingest stamps the owner as issued, so a chunk whose owner differs only in case belongs to
/// someone else and must survive.
/// </remarks>
public sealed class FaissVectorStoreErasureTests
{
    private static readonly float[] Embedding = [0.5f, 0.5f, 0.5f];

    private readonly FaissVectorStore _store = new(NullLogger<FaissVectorStore>.Instance);

    [Fact]
    public async Task EraseSubjectAsync_TwoOwnersAcrossCollections_RemovesOnlyTheSubjectsChunks()
    {
        await _store.IndexAsync([Stamped("a1", "user-a"), Stamped("b1", "user-b"), Stamped("n1", null)], "coll-1");
        await _store.IndexAsync([Stamped("a2", "user-a"), Stamped("b2", "user-b")], "coll-2");
        await _store.IndexAsync([Stamped("a3", "user-a")]);
        (await IdsAsync("coll-1")).Should().BeEquivalentTo(["a1", "b1", "n1"], "the seed must exist before erasing");

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("user-a", []), CancellationToken.None);

        removed.Should().Be(3, "the person's chunks in every collection, default included");
        (await IdsAsync("coll-1")).Should().BeEquivalentTo(["b1", "n1"]);
        (await IdsAsync("coll-2")).Should().BeEquivalentTo(["b2"]);
        (await IdsAsync(null)).Should().BeEmpty();
    }

    [Fact]
    public async Task EraseSubjectAsync_OwnerDiffersOnlyInCase_RemovesOnlyTheExactOwner()
    {
        await _store.IndexAsync([Stamped("upper", "Alice"), Stamped("lower", "alice")]);

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("Alice", []), CancellationToken.None);

        removed.Should().Be(1);
        (await IdsAsync(null)).Should().Equal(["lower"], "a differently-cased owner is a different person");
    }

    [Fact]
    public async Task EraseSubjectAsync_OwnerWithNothingIndexed_ReturnsZeroAndChangesNothing()
    {
        await _store.IndexAsync([Stamped("b1", "user-b")], "coll-1");

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("nobody", []), CancellationToken.None);

        removed.Should().Be(0);
        (await IdsAsync("coll-1")).Should().Equal(["b1"]);
    }

    private async Task<IEnumerable<string>> IdsAsync(string? collection) =>
        (await _store.SearchAsync(Embedding, topK: 50, collection)).Select(r => r.Chunk.Id);

    private static DocumentChunk Stamped(string id, string? ownerId)
    {
        var chunk = RagTestData.CreateChunk(id: id, documentId: $"doc-{id}");
        return chunk with
        {
            Embedding = Embedding,
            Metadata = chunk.Metadata with { OwnerId = ownerId, TenantId = "tenant-a" },
        };
    }
}

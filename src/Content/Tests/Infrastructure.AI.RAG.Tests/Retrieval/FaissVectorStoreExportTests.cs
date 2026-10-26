using Domain.AI.RAG.Models;
using Domain.Common.Compliance;
using FluentAssertions;
using Infrastructure.AI.RAG.Retrieval;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Retrieval;

/// <summary>
/// Personal-data export (#902) for <see cref="FaissVectorStore"/>: the copy holds every chunk the person ingested, in every
/// collection, matched exactly as erasure matches, and nothing of anyone else's.
/// </summary>
public sealed class FaissVectorStoreExportTests
{
    private static readonly float[] Embedding = [0.5f, 0.5f, 0.5f];

    private readonly FaissVectorStore _store = new(NullLogger<FaissVectorStore>.Instance);

    [Fact]
    public async Task ExportSubjectAsync_TwoOwnersAcrossCollections_ReturnsOnlyTheSubjectsChunks()
    {
        await _store.IndexAsync([Stamped("a1", "user-a"), Stamped("b1", "user-b"), Stamped("n1", null)], "coll-1");
        await _store.IndexAsync([Stamped("a2", "user-a")], "coll-2");
        await _store.IndexAsync([Stamped("a3", "user-a")]);

        var records = await _store.ExportSubjectAsync(new ExportSubject("user-a", []), CancellationToken.None);

        records.Select(r => (r.GetProperty("collection").GetString(), r.GetProperty("id").GetString()))
            .Should().BeEquivalentTo([("coll-1", "a1"), ("coll-2", "a2"), ("default", "a3")]);
        records.Should().AllSatisfy(r =>
        {
            r.GetProperty("documentId").GetString().Should().StartWith("doc-a");
            r.GetProperty("content").GetString().Should().NotBeNullOrEmpty();
            r.TryGetProperty("embedding", out _).Should().BeFalse("a derived vector of the same text is not handed over");
            r.GetRawText().Should().NotContain("tenant-a", "the owner and tenant stamps stay in storage");
        });
    }

    [Fact]
    public async Task ExportSubjectAsync_OwnerDiffersOnlyInCase_ReturnsOnlyTheExactOwner()
    {
        await _store.IndexAsync([Stamped("upper", "Alice"), Stamped("lower", "alice")]);

        var records = await _store.ExportSubjectAsync(new ExportSubject("Alice", []), CancellationToken.None);

        records.Select(r => r.GetProperty("id").GetString()).Should().Equal(["upper"],
            "ingest stamps the owner as issued, and a differently-cased owner is a different person");
    }

    [Fact]
    public async Task ExportSubjectAsync_HoldsExactlyWhatErasureThenDeletes()
    {
        await _store.IndexAsync([Stamped("a1", "user-a"), Stamped("b1", "user-b")], "coll-1");
        await _store.IndexAsync([Stamped("a2", "user-a"), Stamped("A3", "User-A")], "coll-2");

        var exported = await _store.ExportSubjectAsync(new ExportSubject("user-a", []), CancellationToken.None);
        var erased = await _store.EraseSubjectAsync(new ErasureSubject("user-a", []), CancellationToken.None);

        exported.Should().HaveCount(erased).And.HaveCount(2);
    }

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

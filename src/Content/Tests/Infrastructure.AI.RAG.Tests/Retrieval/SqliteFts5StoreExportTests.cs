using Domain.AI.RAG.Models;
using Domain.Common.Compliance;
using FluentAssertions;
using Infrastructure.AI.RAG.Retrieval;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Retrieval;

/// <summary>
/// Personal-data export (#902) for <see cref="SqliteFts5Store"/>: the copy holds every chunk the person ingested, in every
/// collection, matched exactly as erasure matches, and nothing of anyone else's.
/// </summary>
/// <remarks>
/// A store that has not ingested yet must still read the table: search answers nothing before the first ingest, and an
/// export that went that way would hand over an empty section that reads as success while the database holds the
/// person's chunks. Each test uses its own in-memory database; the default one is shared across the process.
/// </remarks>
public sealed class SqliteFts5StoreExportTests : IDisposable
{
    private readonly string _connectionString = $"Data Source=Fts5Export-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;
    private readonly SqliteFts5Store _store;

    public SqliteFts5StoreExportTests()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        _store = new SqliteFts5Store(_connectionString, NullLogger<SqliteFts5Store>.Instance);
    }

    public void Dispose() => _keepAlive.Dispose();

    [Fact]
    public async Task ExportSubjectAsync_TwoOwnersAcrossCollections_ReturnsOnlyTheSubjectsChunks()
    {
        await _store.IndexAsync([Stamped("a1", "user-a"), Stamped("b1", "user-b"), Stamped("n1", null)], "coll-1");
        await _store.IndexAsync([Stamped("a2", "user-a")], "coll-2");
        await _store.IndexAsync([Stamped("a3", "user-a")]);

        var records = await _store.ExportSubjectAsync(new ExportSubject("user-a", []), CancellationToken.None);

        records.Select(r => (r.GetProperty("collection").GetString(), r.GetProperty("id").GetString()))
            .Should().BeEquivalentTo([("coll-1", "a1"), ("coll-2", "a2"), ("default", "a3")]);
        records.Should().AllSatisfy(r => r.GetRawText().Should().NotContain("tenant-a", "the stamps stay in storage"));
    }

    [Fact]
    public async Task ExportSubjectAsync_StoreThatHasNotIngested_StillReadsTheDatabase()
    {
        await _store.IndexAsync([Stamped("a1", "user-a")]);
        var freshHost = new SqliteFts5Store(_connectionString, NullLogger<SqliteFts5Store>.Instance);

        var records = await freshHost.ExportSubjectAsync(new ExportSubject("user-a", []), CancellationToken.None);

        records.Should().ContainSingle("a fresh host over the same database must not answer an empty copy");
    }

    [Fact]
    public async Task ExportSubjectAsync_OwnerDiffersOnlyInCase_ReturnsOnlyTheExactOwner()
    {
        await _store.IndexAsync([Stamped("upper", "Alice"), Stamped("lower", "alice")]);

        var records = await _store.ExportSubjectAsync(new ExportSubject("Alice", []), CancellationToken.None);

        records.Select(r => r.GetProperty("id").GetString()).Should().Equal(["upper"]);
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

    [Fact]
    public async Task ExportSubjectAsync_NothingEverIndexed_ReturnsNothingWithoutFailing()
    {
        (await _store.ExportSubjectAsync(new ExportSubject("user-a", []), CancellationToken.None)).Should().BeEmpty();
    }

    private static DocumentChunk Stamped(string id, string? ownerId)
    {
        var chunk = RagTestData.CreateChunk(id: id, documentId: $"doc-{id}");
        return chunk with { Metadata = chunk.Metadata with { OwnerId = ownerId, TenantId = "tenant-a" } };
    }
}

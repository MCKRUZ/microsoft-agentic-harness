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
/// Right-to-erasure (#900) for <see cref="SqliteFts5Store"/>: every row stamped with the person's owner id is deleted
/// from every collection, and every other row — another owner's, an unstamped one — is left in the table.
/// </summary>
/// <remarks>
/// Over-deletion destroys another person's data; under-deletion leaves the person's data behind while the erasure
/// receipt claims success. Rows are read straight from the table because search never returns the owner stamp. Ingest
/// stamps the owner as issued, so a row whose owner differs only in case belongs to someone else and must survive.
/// </remarks>
public sealed class SqliteFts5StoreErasureTests
{
    private readonly string _connectionString = $"Data Source=Fts5Erase-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteFts5Store _store;

    public SqliteFts5StoreErasureTests() =>
        _store = new SqliteFts5Store(_connectionString, NullLogger<SqliteFts5Store>.Instance);

    [Fact]
    public async Task EraseSubjectAsync_TwoOwnersAcrossCollections_RemovesOnlyTheSubjectsRows()
    {
        await _store.IndexAsync([Stamped("a1", "user-a"), Stamped("b1", "user-b"), Stamped("n1", null)], "coll-1");
        await _store.IndexAsync([Stamped("a2", "user-a"), Stamped("b2", "user-b")], "coll-2");
        await _store.IndexAsync([Stamped("a3", "user-a")]);
        (await RowsAsync()).Should().HaveCount(6, "the seed must exist before erasing");

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("user-a", []), CancellationToken.None);

        removed.Should().Be(3, "the person's rows in every collection, default included");
        (await RowsAsync()).Should().BeEquivalentTo([("b1", "user-b"), ("n1", (string?)null), ("b2", "user-b")]);
    }

    [Fact]
    public async Task EraseSubjectAsync_OwnerDiffersOnlyInCase_RemovesOnlyTheExactOwner()
    {
        await _store.IndexAsync([Stamped("upper", "Alice"), Stamped("lower", "alice")]);

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("Alice", []), CancellationToken.None);

        removed.Should().Be(1);
        (await RowsAsync()).Should().Equal([("lower", "alice")], "a differently-cased owner is a different person");
    }

    [Fact]
    public async Task EraseSubjectAsync_NothingEverIndexed_ReturnsZeroWithoutFailing()
    {
        var removed = await _store.EraseSubjectAsync(new ErasureSubject("user-a", []), CancellationToken.None);

        removed.Should().Be(0, "a store that has not ingested yet answers truthfully instead of throwing");
    }

    [Fact]
    public async Task EraseSubjectAsync_OwnerWithNothingIndexed_ReturnsZeroAndChangesNothing()
    {
        await _store.IndexAsync([Stamped("b1", "user-b")], "coll-1");

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("nobody", []), CancellationToken.None);

        removed.Should().Be(0);
        (await RowsAsync()).Should().Equal([("b1", "user-b")]);
    }

    private async Task<List<(string Id, string? OwnerId)>> RowsAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, owner_id FROM chunks_fts";
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, string?)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return rows;
    }

    private static DocumentChunk Stamped(string id, string? ownerId)
    {
        var chunk = RagTestData.CreateChunk(id: id, content: "erasable content body", documentId: $"doc-{id}");
        return chunk with { Metadata = chunk.Metadata with { OwnerId = ownerId, TenantId = "tenant-a" } };
    }
}

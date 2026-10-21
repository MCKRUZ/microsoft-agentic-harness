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

    [Fact]
    public async Task EraseSubjectAsync_OnDisk_LeavesNoTraceOfTheErasedWordsInTheFile()
    {
        // FTS5 normally records a deletion as a tombstone and keeps the old words in its index until a merge, and SQLite
        // leaves deleted rows in free pages, so erased text stayed readable in the file. The bystander's word is the
        // control: the scan must find it, or it proves nothing about the subject's.
        var directory = Path.Combine(Path.GetTempPath(), "fts5-scrub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "fts.db");
        var store = new SqliteFts5Store($"Data Source={file}", NullLogger<SqliteFts5Store>.Instance);

        // The scan looks for each marker's random tail, not the whole word: FTS5 stores an index term compressed against
        // the term before it, so a whole word that shares a first letter with its neighbour is never contiguous in the
        // file, while a random tail always is. Searching for the whole word could not see the index at all.
        var subjectTail = Guid.NewGuid().ToString("N")[..10];
        var bystanderTail = Guid.NewGuid().ToString("N")[..10];
        await store.IndexAsync(
        [
            WithContent("s1", "user-a", $"subjectmark{subjectTail} appears once"),
            WithContent("b1", "user-b", $"bystandermark{bystanderTail} appears once"),
        ]);
        await store.EraseSubjectAsync(new ErasureSubject("user-a", []), CancellationToken.None);

        var bytes = ReadShared(file);
        Occurrences(bytes, bystanderTail).Should().BeGreaterThanOrEqualTo(2, "the bystander's row and index term must both be seen");
        Occurrences(bytes, subjectTail).Should().Be(0, "neither the row nor the index may keep the erased words");
        SqliteConnection.ClearAllPools();
    }

    private static int Occurrences(byte[] haystack, string text)
    {
        var needle = System.Text.Encoding.UTF8.GetBytes(text);
        var count = 0;
        for (var span = haystack.AsSpan(); span.IndexOf(needle) is var at and >= 0; span = span[(at + needle.Length)..])
            count++;
        return count;
    }

    private static byte[] ReadShared(string path)
    {
        // The store keeps a connection open, so the file is read without asking for exclusive access.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static DocumentChunk WithContent(string id, string ownerId, string content)
    {
        var chunk = RagTestData.CreateChunk(id: id, content: content, documentId: $"doc-{id}");
        return chunk with { Metadata = chunk.Metadata with { OwnerId = ownerId, TenantId = "tenant-a" } };
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

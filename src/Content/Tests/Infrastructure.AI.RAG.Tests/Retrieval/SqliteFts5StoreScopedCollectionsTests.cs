using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.Retrieval;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Retrieval;

/// <summary>
/// Verifies the K4 store behaviors of <see cref="SqliteFts5Store"/>: genuine collection
/// partitioning (search and delete scoped to one collection, mirroring
/// <see cref="FaissVectorStore"/> so the local hybrid pipeline's dense and sparse halves
/// agree), owner/tenant provenance stamps persisted for erasure but never exposed on the
/// read path, and safe in-place migration of a legacy pre-collection table.
/// </summary>
public sealed class SqliteFts5StoreScopedCollectionsTests
{
    private static string UniqueConnectionString() =>
        $"Data Source=Fts5Scoped-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private static SqliteFts5Store CreateStore(string connectionString) =>
        new(connectionString, NullLogger<SqliteFts5Store>.Instance);

    private static DocumentChunk StampedChunk(
        string id, string content, string documentId, string? ownerId, string? tenantId)
    {
        var chunk = RagTestData.CreateChunk(id: id, content: content, documentId: documentId);
        return chunk with
        {
            Metadata = chunk.Metadata with { OwnerId = ownerId, TenantId = tenantId },
        };
    }

    /// <summary>Reads persisted column values for a chunk row straight from the database.</summary>
    private static async Task<(string? OwnerId, string? TenantId, string Collection)> ReadRowAsync(
        string connectionString, string chunkId)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT owner_id, tenant_id, collection FROM chunks_fts WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", chunkId);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"row '{chunkId}' should exist");
        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2));
    }

    [Fact]
    public async Task SearchAsync_TwoTenantDerivedCollections_CannotSeeEachOthersChunks()
    {
        var store = CreateStore(UniqueConnectionString());
        var collectionA = ScopedCollectionName.DeriveForTenant("tenant-a");
        var collectionB = ScopedCollectionName.DeriveForTenant("tenant-b");

        await store.IndexAsync(
            [StampedChunk("a1", "confidential alpha report", "doc-a", "user-1", "tenant-a")],
            collectionA);
        await store.IndexAsync(
            [StampedChunk("b1", "confidential beta report", "doc-b", "user-2", "tenant-b")],
            collectionB);

        var seenByA = await store.SearchAsync("confidential report", topK: 10, collectionA);
        var seenByB = await store.SearchAsync("confidential report", topK: 10, collectionB);

        seenByA.Should().ContainSingle().Which.Chunk.Id.Should().Be("a1");
        seenByB.Should().ContainSingle().Which.Chunk.Id.Should().Be("b1",
            "a tenant-derived collection must never surface another tenant's chunks");
    }

    [Fact]
    public async Task SearchAsync_NullCollection_SearchesDefaultCollectionOnly()
    {
        var store = CreateStore(UniqueConnectionString());

        await store.IndexAsync(
            [StampedChunk("g1", "shared corpus knowledge", "doc-g", null, null)]);
        await store.IndexAsync(
            [StampedChunk("t1", "shared corpus knowledge tenant copy", "doc-t", "u", "t")],
            ScopedCollectionName.DeriveForTenant("tenant-a"));

        var results = await store.SearchAsync("shared corpus", topK: 10);

        results.Should().ContainSingle().Which.Chunk.Id.Should().Be("g1",
            "no-identity callers address the closed default collection, not the whole table");
    }

    [Fact]
    public async Task IndexAsync_StampedChunk_PersistsOwnerAndTenantColumns()
    {
        var connectionString = UniqueConnectionString();
        var store = CreateStore(connectionString);
        await store.IndexAsync(
            [StampedChunk("s1", "provenance stamped content", "doc-s", "user-1", "tenant-a")]);

        var row = await ReadRowAsync(connectionString, "s1");

        row.OwnerId.Should().Be("user-1", "the persisted stamp is the future erasure key");
        row.TenantId.Should().Be("tenant-a");
        row.Collection.Should().Be("default");
    }

    [Fact]
    public async Task SearchAsync_StampedChunk_DoesNotExposeStampsOnResults()
    {
        var store = CreateStore(UniqueConnectionString());
        await store.IndexAsync(
            [StampedChunk("s1", "provenance stamped content", "doc-s", "user-1", "tenant-a")]);

        var results = await store.SearchAsync("provenance stamped", topK: 10);

        var metadata = results.Should().ContainSingle().Which.Chunk.Metadata;
        metadata.OwnerId.Should().BeNull(
            "stamps are erasure data; exposing them would tell every searcher who ingested each chunk");
        metadata.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ScopedToCollection_LeavesOtherCollectionsIntact()
    {
        var store = CreateStore(UniqueConnectionString());

        await store.IndexAsync(
            [StampedChunk("d1", "duplicated document text", "doc-1", null, "tenant-a")], "coll-a");
        await store.IndexAsync(
            [StampedChunk("d2", "duplicated document text", "doc-1", null, "tenant-b")], "coll-b");

        await store.DeleteAsync("doc-1", "coll-a");

        (await store.SearchAsync("duplicated document", topK: 10, "coll-a")).Should().BeEmpty();
        (await store.SearchAsync("duplicated document", topK: 10, "coll-b")).Should().ContainSingle(
            "deletion is collection-scoped, mirroring FaissVectorStore");
    }

    [Fact]
    public async Task IndexAsync_LegacyPreCollectionTable_MigratesRowsIntoDefaultCollection()
    {
        var connectionString = UniqueConnectionString();

        // Keep the shared in-memory database alive and create the pre-K4 schema with a row,
        // exactly as a persistent database written by the old store would contain.
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        await using (var cmd = keepAlive.CreateCommand())
        {
            cmd.CommandText = """
                CREATE VIRTUAL TABLE chunks_fts USING fts5(
                    id UNINDEXED, document_id UNINDEXED, content, section_path);
                INSERT INTO chunks_fts(id, document_id, content, section_path)
                VALUES ('legacy-1', 'doc-legacy', 'legacy corpus content survives migration', 'Root');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var store = CreateStore(connectionString);
        await store.IndexAsync(
            [StampedChunk("new-1", "fresh content after migration", "doc-new", "u1", "t1")]);

        var legacyHits = await store.SearchAsync("legacy corpus", topK: 10);
        legacyHits.Should().ContainSingle().Which.Chunk.Id.Should().Be("legacy-1");

        var legacyRow = await ReadRowAsync(connectionString, "legacy-1");
        legacyRow.Collection.Should().Be("default", "legacy rows land in the default collection");
        legacyRow.OwnerId.Should().BeNull("legacy rows carry no stamps");

        // "fresh" is unique to the new row (the FTS query ORs its tokens, so a shared
        // token like "content" would match the legacy row too).
        (await store.SearchAsync("fresh", topK: 10)).Should().ContainSingle(
            "new rows land in the migrated table alongside the copied legacy rows");
    }

    [Fact]
    public async Task EnsureInitialized_SecondStoreOnMigratedDatabase_DoesNotReMigrate()
    {
        var connectionString = UniqueConnectionString();

        // First store initializes the current schema and writes into a named collection.
        var first = CreateStore(connectionString);
        await first.IndexAsync(
            [StampedChunk("n1", "partitioned tenant content", "doc-n", "u1", "tenant-a")],
            "coll-a");

        // A second store instance (e.g. another process on the same database file) must
        // see the already-current schema and leave it alone — a re-migration would
        // collapse every collection into 'default' and null the stamps.
        var second = CreateStore(connectionString);
        await second.IndexAsync(
            [StampedChunk("n2", "other partitioned content", "doc-n2", "u2", "tenant-b")],
            "coll-b");

        var row = await ReadRowAsync(connectionString, "n1");
        row.Collection.Should().Be("coll-a", "re-initialization must not collapse collections");
        row.OwnerId.Should().Be("u1", "re-initialization must not null the stamps");

        (await second.SearchAsync("partitioned tenant", topK: 10, "coll-a"))
            .Should().ContainSingle().Which.Chunk.Id.Should().Be("n1");
    }
}

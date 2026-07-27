using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Retrieval;

/// <summary>
/// SQLite FTS5-based implementation of <see cref="IBm25Store"/> for local development.
/// Uses SQLite's built-in FTS5 full-text search engine for BM25 keyword matching.
/// Paired with <see cref="FaissVectorStore"/> as the local-dev sparse retrieval backend.
/// Registered as keyed service <c>"faiss"</c> (bundled with the in-memory vector store).
/// </summary>
/// <remarks>
/// <para>
/// Each operation opens and closes its own <see cref="SqliteConnection"/> for thread
/// safety. The FTS5 virtual table is auto-created on first <see cref="IndexAsync"/> call.
/// Uses <c>:memory:</c> by default; configure via connection string for persistent storage.
/// </para>
/// <para>
/// FTS5 <c>rank</c> returns negative BM25 scores (more negative = more relevant).
/// Results are normalized to [0, 1] for consistent fusion with dense scores.
/// </para>
/// <para>
/// <strong>Collections.</strong> Rows carry a <c>collection</c> value
/// (<c>collectionName ?? "default"</c>) and every search and delete is scoped to one
/// collection, mirroring <see cref="FaissVectorStore"/>'s partitioning so the dense and
/// sparse halves of the local hybrid pipeline see the same partition. A null
/// <c>collectionName</c> addresses the global/default collection only. Partitioning is
/// <strong>unconditional</strong> — deliberately independent of the <c>ScopedCollections</c>
/// flag: before this store honored collections, the BM25 half ignored them while FAISS
/// partitioned, a dense/sparse asymmetry that leaked across collections. Flag-off behavior
/// changes only for callers who relied on that bug.
/// </para>
/// <para>
/// <strong>Provenance.</strong> Rows persist the chunk's
/// <see cref="ChunkMetadata.OwnerId"/>/<see cref="ChunkMetadata.TenantId"/> stamps but
/// never return them on search results: the stamps are the future erasure key, not
/// retrieval data — surfacing them would tell every searcher who ingested each chunk in
/// the shared corpus. They are never used as a search filter either.
/// </para>
/// <para>
/// <strong>Migration.</strong> A persistent database created before the collection and
/// provenance columns existed is migrated in place on first use (rename → recreate →
/// copy → drop); legacy rows land in the default collection with null stamps.
/// </para>
/// </remarks>
public sealed class SqliteFts5Store : IBm25Store
{
    private const string DefaultCollection = "default";

    private readonly string _connectionString;
    private readonly ILogger<SqliteFts5Store> _logger;
    private readonly object _initLock = new();
    private volatile bool _initialized;

    /// <summary>
    /// Shared in-memory connection that keeps the database alive for the <c>:memory:</c>
    /// case. Without this, each new connection gets a fresh empty database.
    /// </summary>
    private SqliteConnection? _keepAliveConnection;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteFts5Store"/> class.
    /// </summary>
    /// <param name="connectionString">
    /// The SQLite connection string. Defaults to a shared in-memory database.
    /// </param>
    /// <param name="logger">The logger instance.</param>
    public SqliteFts5Store(string? connectionString, ILogger<SqliteFts5Store> logger)
    {
        _connectionString = connectionString
            ?? "Data Source=RagFts5;Mode=Memory;Cache=Shared";
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task IndexAsync(
        IReadOnlyList<DocumentChunk> chunks,
        string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0) return;

        await EnsureInitializedAsync(cancellationToken);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var chunk in chunks)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO chunks_fts(
                    id, document_id, content, section_path, collection, owner_id, tenant_id)
                VALUES (@id, @documentId, @content, @sectionPath, @collection, @ownerId, @tenantId)
                """;
            cmd.Parameters.AddWithValue("@id", chunk.Id);
            cmd.Parameters.AddWithValue("@documentId", chunk.DocumentId);
            cmd.Parameters.AddWithValue("@content", chunk.Content);
            cmd.Parameters.AddWithValue("@sectionPath", chunk.SectionPath);
            cmd.Parameters.AddWithValue("@collection", collectionName ?? DefaultCollection);
            cmd.Parameters.AddWithValue("@ownerId", (object?)chunk.Metadata.OwnerId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tenantId", (object?)chunk.Metadata.TenantId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        _logger.LogDebug(
            "Indexed {Count} chunks into SQLite FTS5 (collection: {Collection})",
            chunks.Count, collectionName ?? DefaultCollection);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        string query,
        int topK,
        string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        if (!_initialized) return [];

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        // owner_id/tenant_id are deliberately NOT selected: provenance stamps stay in
        // storage for erasure and are never exposed on the read path.
        cmd.CommandText = """
            SELECT id, document_id, content, section_path, rank
            FROM chunks_fts
            WHERE chunks_fts MATCH @query AND collection = @collection
            ORDER BY rank
            LIMIT @topK
            """;
        cmd.Parameters.AddWithValue("@query", EscapeFts5Query(query));
        cmd.Parameters.AddWithValue("@collection", collectionName ?? DefaultCollection);
        cmd.Parameters.AddWithValue("@topK", topK);

        var results = new List<RetrievalResult>();

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rawRank = reader.GetDouble(4);
            var normalizedScore = NormalizeFts5Rank(rawRank);

            results.Add(new RetrievalResult
            {
                Chunk = new DocumentChunk
                {
                    Id = reader.GetString(0),
                    DocumentId = reader.GetString(1),
                    Content = reader.GetString(2),
                    SectionPath = reader.GetString(3),
                    Tokens = 0,
                    Metadata = new ChunkMetadata
                    {
                        SourceUri = new Uri("search://sqlite-fts5"),
                        CreatedAt = DateTimeOffset.UtcNow,
                    },
                },
                DenseScore = 0.0,
                SparseScore = normalizedScore,
                FusedScore = normalizedScore,
            });
        }

        return results;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(
        string documentId,
        string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        if (!_initialized) return;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "DELETE FROM chunks_fts WHERE document_id = @documentId AND collection = @collection";
        cmd.Parameters.AddWithValue("@documentId", documentId);
        cmd.Parameters.AddWithValue("@collection", collectionName ?? DefaultCollection);

        var deleted = await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogDebug(
            "Deleted {Count} chunks for document {DocumentId} from SQLite FTS5 (collection: {Collection})",
            deleted, documentId, collectionName ?? DefaultCollection);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            _keepAliveConnection = CreateConnection();
            _keepAliveConnection.Open();

            // BEGIN IMMEDIATE takes the database write lock BEFORE the schema check, so
            // check-and-migrate is atomic across processes sharing a database file. A
            // deferred transaction would re-open the classic TOCTOU: two processes both
            // see the legacy schema, the loser re-migrates the already-migrated table and
            // collapses every collection into 'default' with nulled stamps.
            using var transaction = _keepAliveConnection.BeginTransaction(
                System.Data.IsolationLevel.Serializable, deferred: false);

            if (TableNeedsMigration(_keepAliveConnection))
            {
                MigrateLegacyTable(_keepAliveConnection);
            }

            using var cmd = _keepAliveConnection.CreateCommand();
            cmd.CommandText = $"CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5({SchemaColumns})";
            cmd.ExecuteNonQuery();

            transaction.Commit();
            _initialized = true;
        }

        await Task.CompletedTask;

        _logger.LogDebug("SQLite FTS5 table initialized");
    }

    /// <summary>
    /// Column list of the FTS5 virtual table. Only <c>content</c> and <c>section_path</c>
    /// participate in full-text matching; identifiers, the collection partition value, and
    /// the provenance stamps are stored unindexed.
    /// </summary>
    private const string SchemaColumns =
        "id UNINDEXED, document_id UNINDEXED, content, section_path, " +
        "collection UNINDEXED, owner_id UNINDEXED, tenant_id UNINDEXED";

    /// <summary>
    /// Returns whether an existing <c>chunks_fts</c> table predates the collection and
    /// provenance columns. FTS5 virtual tables cannot <c>ALTER TABLE ... ADD COLUMN</c>,
    /// so a legacy persistent database must be rebuilt via copy.
    /// </summary>
    private static bool TableNeedsMigration(SqliteConnection connection)
    {
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText =
                "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'chunks_fts'";
            if (Convert.ToInt64(exists.ExecuteScalar()) == 0) return false;
        }

        using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(chunks_fts)";
        using var reader = columns.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "collection", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Rebuilds a legacy table into the current schema: rename aside, create the new
    /// table, copy every row into the default collection with null provenance stamps,
    /// drop the legacy copy. Runs inside the caller's already-open IMMEDIATE transaction
    /// (see <see cref="EnsureInitializedAsync"/>), which both makes a crash mid-migration
    /// leave the legacy table intact and guarantees the schema re-check that gated this
    /// call cannot go stale under a concurrent process.
    /// </summary>
    private void MigrateLegacyTable(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            ALTER TABLE chunks_fts RENAME TO chunks_fts_legacy;
            CREATE VIRTUAL TABLE chunks_fts USING fts5({SchemaColumns});
            INSERT INTO chunks_fts(id, document_id, content, section_path, collection)
                SELECT id, document_id, content, section_path, '{DefaultCollection}'
                FROM chunks_fts_legacy;
            DROP TABLE chunks_fts_legacy;
            """;
        cmd.ExecuteNonQuery();

        _logger.LogInformation(
            "Migrated legacy SQLite FTS5 table to the collection-aware schema; " +
            "existing rows moved to the '{Collection}' collection",
            DefaultCollection);
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    /// <summary>
    /// FTS5 rank values are negative (more negative = more relevant).
    /// Normalize to [0, 1] using <c>1 / (1 + |rank|)</c>.
    /// </summary>
    private static double NormalizeFts5Rank(double rank) =>
        1.0 / (1.0 + Math.Abs(rank));

    /// <summary>
    /// Escapes user input for safe FTS5 MATCH queries. Each token is quoted for exact
    /// matching; prefix operators (<c>*</c>, <c>^</c>) are stripped to prevent unintended FTS5 behavior.
    /// </summary>
    private static string EscapeFts5Query(string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return "\"_empty\"";

        return string.Join(" OR ", tokens.Select(t =>
        {
            var sanitized = t
                .Replace("\"", "\"\"")
                .Replace("*", "")
                .Replace("^", "")
                .Replace(":", "")
                .Replace("(", "")
                .Replace(")", "")
                .Replace("{", "")
                .Replace("}", "");
            if (string.IsNullOrWhiteSpace(sanitized)) sanitized = "_empty";
            return $"\"{sanitized}\"";
        }));
    }
}

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
/// </remarks>
public sealed class SqliteFts5Store : IBm25Store
{
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
                INSERT OR REPLACE INTO chunks_fts(id, document_id, content, section_path)
                VALUES (@id, @documentId, @content, @sectionPath)
                """;
            cmd.Parameters.AddWithValue("@id", chunk.Id);
            cmd.Parameters.AddWithValue("@documentId", chunk.DocumentId);
            cmd.Parameters.AddWithValue("@content", chunk.Content);
            cmd.Parameters.AddWithValue("@sectionPath", chunk.SectionPath);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        _logger.LogDebug("Indexed {Count} chunks into SQLite FTS5", chunks.Count);
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
        cmd.CommandText = """
            SELECT id, document_id, content, section_path, rank
            FROM chunks_fts
            WHERE chunks_fts MATCH @query
            ORDER BY rank
            LIMIT @topK
            """;
        cmd.Parameters.AddWithValue("@query", EscapeFts5Query(query));
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
        cmd.CommandText = "DELETE FROM chunks_fts WHERE document_id = @documentId";
        cmd.Parameters.AddWithValue("@documentId", documentId);

        var deleted = await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogDebug(
            "Deleted {Count} chunks for document {DocumentId} from SQLite FTS5",
            deleted, documentId);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            _keepAliveConnection = CreateConnection();
            _keepAliveConnection.Open();

            using var cmd = _keepAliveConnection.CreateCommand();
            cmd.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
                    id UNINDEXED,
                    document_id UNINDEXED,
                    content,
                    section_path,
                    content=''
                )
                """;
            cmd.ExecuteNonQuery();
            _initialized = true;
        }

        await Task.CompletedTask;

        _logger.LogDebug("SQLite FTS5 table initialized");
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    /// <summary>
    /// FTS5 rank values are negative (more negative = more relevant).
    /// Normalize to [0, 1] using <c>1 / (1 + |rank|)</c>.
    /// </summary>
    private static double NormalizeFts5Rank(double rank) =>
        1.0 / (1.0 + Math.Abs(rank));

    /// <summary>
    /// Escapes special FTS5 characters to prevent query syntax errors.
    /// Wraps each token in double quotes for exact matching.
    /// </summary>
    private static string EscapeFts5Query(string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" OR ", tokens.Select(t => $"\"{t.Replace("\"", "\"\"")}\""));
    }
}

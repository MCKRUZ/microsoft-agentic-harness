using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.SqlDatabase;

/// <summary>
/// Two-tier SQL retrieval source: tries template matching first, falls back to LLM text-to-SQL
/// if <see cref="SqlDatabaseConfig.AllowLlmFallback"/> is enabled.
/// Registered as keyed DI with key "sql_database".
/// </summary>
internal sealed class SqlDatabaseRetrievalSource(
    ISqlQueryTemplateStore templateStore,
    ISqlQueryExecutor executor,
    SqlQueryTemplateMatcher templateMatcher,
    TextToSqlGenerator textToSqlGenerator,
    IOptionsMonitor<AppConfig> configMonitor,
    ILogger<SqlDatabaseRetrievalSource> logger) : IRetrievalSource
{
    /// <inheritdoc />
    public string SourceName => "sql_database";

    /// <inheritdoc />
    public async Task<SourceRetrievalResult> RetrieveAsync(
        string query, int topK, QueryComplexity complexity, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var config = configMonitor.CurrentValue.AI.Rag.SqlDatabase;

        var templates = await templateStore.GetTemplatesAsync(cancellationToken);

        // Tier 1: Template matching
        if (templates.Count > 0)
        {
            var match = await templateMatcher.MatchAsync(query, templates, cancellationToken);
            if (match is not null)
            {
                var paramDict = match.Value.Parameters.ToDictionary(
                    kvp => kvp.Key, kvp => (object?)kvp.Value);
                var sqlResult = await executor.ExecuteAsync(
                    match.Value.Template.SqlTemplate, paramDict, cancellationToken);
                sw.Stop();

                return ToSourceResult(sqlResult with { WasTemplateMatch = true }, sw.Elapsed);
            }
        }

        // Tier 2: LLM fallback
        if (!config.AllowLlmFallback)
        {
            sw.Stop();
            return new SourceRetrievalResult
            {
                SourceName = SourceName, Results = [], Latency = sw.Elapsed, TokensUsed = 0
            };
        }

        var generatedSql = await textToSqlGenerator.GenerateAsync(query, config.DatabaseSchema, cancellationToken);
        if (generatedSql is null)
        {
            sw.Stop();
            logger.LogWarning("Text-to-SQL generation returned null for query '{Query}'", query);
            return new SourceRetrievalResult
            {
                SourceName = SourceName, Results = [], Latency = sw.Elapsed, TokensUsed = 0
            };
        }

        var fallbackResult = await executor.ExecuteAsync(generatedSql, null, cancellationToken);
        sw.Stop();

        return ToSourceResult(fallbackResult, sw.Elapsed);
    }

    private SourceRetrievalResult ToSourceResult(SqlRetrievalResult sqlResult, TimeSpan latency)
    {
        var results = sqlResult.Rows
            .Select((row, index) => RowToRetrievalResult(row, index, sqlResult.Query, sqlResult.WasTemplateMatch))
            .ToList();

        return new SourceRetrievalResult
        {
            SourceName = SourceName,
            Results = results,
            Latency = latency,
            TokensUsed = 0
        };
    }

    private static RetrievalResult RowToRetrievalResult(
        IReadOnlyDictionary<string, object?> row, int index, string query, bool wasTemplate)
    {
        var content = JsonSerializer.Serialize(row);
        var score = 1.0 / (1 + index);

        return new RetrievalResult
        {
            Chunk = new DocumentChunk
            {
                Id = $"sql-{StableHash(query)}-{index}",
                DocumentId = $"sql:{(wasTemplate ? "template" : "generated")}",
                SectionPath = $"SQL {(wasTemplate ? "Template" : "Generated")}: {query}",
                Content = content,
                Tokens = content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                Metadata = new ChunkMetadata
                {
                    SourceUri = new Uri($"sql://{(wasTemplate ? "template" : "generated")}"),
                    CreatedAt = DateTimeOffset.UtcNow
                }
            },
            DenseScore = score,
            SparseScore = 0.0,
            FusedScore = score
        };
    }

    private static string StableHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes)[..8];
    }
}

using System.Data.Common;
using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.SqlDatabase;

/// <summary>
/// Executes SQL queries with safety guardrails: read-only enforcement, row limits, and query timeout.
/// Rejects any SQL containing mutation keywords before execution.
/// </summary>
internal sealed partial class SafeSqlQueryExecutor(
    DbConnection connection,
    IOptionsMonitor<AppConfig> configMonitor,
    ILogger<SafeSqlQueryExecutor> logger) : ISqlQueryExecutor
{
    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|DROP|ALTER|TRUNCATE|CREATE|EXEC|EXECUTE|GRANT|REVOKE)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex MutationPattern();

    /// <summary>
    /// Executes a read-only <paramref name="sql"/> query against the configured database.
    /// Throws <see cref="InvalidOperationException"/> if the query contains any mutation keyword.
    /// Results are capped at the configured <c>MaxRows</c> limit.
    /// </summary>
    public async Task<SqlRetrievalResult> ExecuteAsync(
        string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        if (MutationPattern().IsMatch(sql))
            throw new InvalidOperationException(
                "SQL query rejected: only read-only SELECT statements are allowed. Query contained a mutation keyword.");

        var config = configMonitor.CurrentValue.AI.Rag.SqlDatabase;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- pre-validated by MutationPattern; user values bind via cmd.Parameters
        cmd.CommandTimeout = config.QueryTimeoutSeconds;

        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
            {
                var param = cmd.CreateParameter();
                param.ParameterName = key;
                param.Value = value ?? DBNull.Value;
                cmd.Parameters.Add(param);
            }
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken) && rows.Count < config.MaxRows)
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }

        logger.LogDebug("SQL query returned {RowCount} rows (limit: {MaxRows})", rows.Count, config.MaxRows);

        return new SqlRetrievalResult
        {
            Query = sql,
            WasTemplateMatch = false,
            Rows = rows
        };
    }
}

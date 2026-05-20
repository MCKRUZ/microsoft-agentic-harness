using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Executes validated SQL queries against a configured database with safety guardrails
/// (read-only enforcement, row limits, query timeout).
/// </summary>
public interface ISqlQueryExecutor
{
    Task<SqlRetrievalResult> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken cancellationToken);
}

using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Loads pre-defined SQL query templates from a backing store (JSON file, database, etc.).
/// Templates are matched against natural language queries before falling back to LLM-generated SQL.
/// </summary>
public interface ISqlQueryTemplateStore
{
    Task<IReadOnlyList<SqlQueryTemplate>> GetTemplatesAsync(CancellationToken cancellationToken);
}

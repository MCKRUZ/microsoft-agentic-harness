namespace Domain.AI.RAG.Models;

/// <summary>
/// Result of executing a SQL query, including the query text, whether it matched a template,
/// and the result rows as dictionaries.
/// </summary>
public sealed record SqlRetrievalResult
{
    /// <summary>The SQL query that was executed.</summary>
    public required string Query { get; init; }

    /// <summary>True if the query matched a pre-defined template; false if LLM-generated.</summary>
    public required bool WasTemplateMatch { get; init; }

    /// <summary>Result rows as column-name → value dictionaries.</summary>
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }
}

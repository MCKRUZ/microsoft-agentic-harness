namespace Domain.AI.RAG.Models;

/// <summary>
/// A pre-defined parameterized SQL query template. Matched against natural language queries
/// before falling back to LLM-generated SQL.
/// </summary>
public sealed record SqlQueryTemplate
{
    /// <summary>Unique template identifier (e.g., "orders_by_date").</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description used by the LLM to match queries to templates.</summary>
    public required string Description { get; init; }

    /// <summary>Parameterized SQL (e.g., "SELECT * FROM orders WHERE date >= @startDate").</summary>
    public required string SqlTemplate { get; init; }

    /// <summary>Parameter names expected by the template (e.g., ["startDate", "endDate"]).</summary>
    public required IReadOnlyList<string> Parameters { get; init; }
}

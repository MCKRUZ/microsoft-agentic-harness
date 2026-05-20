namespace Domain.AI.RAG.Models;

/// <summary>
/// A single result from a web search provider (Bing, Tavily, Google).
/// </summary>
public sealed record WebSearchResult
{
    /// <summary>Page title.</summary>
    public required string Title { get; init; }

    /// <summary>Search engine snippet or extracted summary.</summary>
    public required string Snippet { get; init; }

    /// <summary>Full URL of the result page.</summary>
    public required string Url { get; init; }

    /// <summary>Full-text content if the provider supports extraction (e.g., Tavily). Null otherwise.</summary>
    public string? Content { get; init; }
}

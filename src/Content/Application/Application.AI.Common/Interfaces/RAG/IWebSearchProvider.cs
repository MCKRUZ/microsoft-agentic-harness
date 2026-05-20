using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Provider-agnostic web search contract. Implementations: Bing, Tavily, Google.
/// Registered via keyed DI with the provider name (e.g., "bing").
/// </summary>
public interface IWebSearchProvider
{
    /// <summary>
    /// Executes a web search and returns structured results with title, snippet, URL, and optional full content.
    /// </summary>
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken);
}

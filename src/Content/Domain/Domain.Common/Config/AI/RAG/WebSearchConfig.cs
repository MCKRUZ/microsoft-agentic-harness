namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for the web search retrieval source.
/// Bound to <c>AppConfig:AI:Rag:WebSearch</c>.
/// </summary>
public sealed class WebSearchConfig
{
    /// <summary>Provider key matching a keyed DI registration (e.g., "bing", "tavily").</summary>
    public string Provider { get; set; } = "bing";

    /// <summary>Provider endpoint override. Null uses the provider's default.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Maximum results per query.</summary>
    public int MaxResults { get; set; } = 5;

    /// <summary>Search market/locale (e.g., "en-US").</summary>
    public string Market { get; set; } = "en-US";

    /// <summary>Safe search level: Off, Moderate, Strict.</summary>
    public string SafeSearch { get; set; } = "Moderate";
}

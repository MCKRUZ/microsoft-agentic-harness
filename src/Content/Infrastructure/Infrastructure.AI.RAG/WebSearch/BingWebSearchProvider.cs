using System.Text.Json;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.WebSearch;

/// <summary>
/// Calls Bing Search API v7. API key must be provided via User Secrets or Key Vault,
/// injected as a named HttpClient with the <c>Ocp-Apim-Subscription-Key</c> header pre-configured.
/// </summary>
internal sealed class BingWebSearchProvider(
    HttpClient httpClient,
    IOptionsMonitor<AppConfig> configMonitor,
    ILogger<BingWebSearchProvider> logger) : IWebSearchProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken)
    {
        var config = configMonitor.CurrentValue.AI.Rag.WebSearch;
        var requestUri = $"v7.0/search?q={Uri.EscapeDataString(query)}&count={maxResults}&mkt={config.Market}&safeSearch={config.SafeSearch}";

        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(requestUri, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bing Search API request failed for query '{Query}'", query);
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Bing Search API returned {StatusCode} for query '{Query}'",
                response.StatusCode, query);
            return [];
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var bingResponse = JsonSerializer.Deserialize<BingSearchResponse>(content, JsonOptions);

        if (bingResponse?.WebPages?.Value is null or { Length: 0 })
            return [];

        return bingResponse.WebPages.Value
            .Select(v => new WebSearchResult
            {
                Title = v.Name ?? "",
                Snippet = v.Snippet ?? "",
                Url = v.Url ?? "",
                Content = null
            })
            .ToList();
    }

    private sealed record BingSearchResponse(BingWebPages? WebPages);
    private sealed record BingWebPages(BingWebPage[] Value);
    private sealed record BingWebPage(string? Name, string? Snippet, string? Url);
}

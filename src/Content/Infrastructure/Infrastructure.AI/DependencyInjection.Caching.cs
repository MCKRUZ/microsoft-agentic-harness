using System.Net.Http.Headers;
using Application.AI.Common.Interfaces;
using Domain.Common.Config.AI;
using Infrastructure.AI.Caching;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    private const int GenerationStatsTimeoutSeconds = 30;

    /// <summary>
    /// Registers the <see cref="IGenerationStatsClient"/> used to fetch out-of-band prompt-cache
    /// telemetry, but only on the OpenRouter path with prompt caching enabled — the one shape where
    /// cache token counts are unavailable on the inline response and the
    /// <c>GET /generation?id=</c> endpoint exists to supply them.
    /// </summary>
    /// <remarks>
    /// When the conditions are not met the service is left unregistered, so
    /// <c>AgentFactory.BuildMiddlewarePipeline</c> resolves <see langword="null"/> and skips the
    /// cache-enrichment middleware entirely. This keeps the feature inert for every other provider
    /// (Azure OpenAI caches automatically; native Anthropic reports cache tokens inline) and avoids
    /// an extra per-call HTTP request when caching is off.
    /// </remarks>
    private static void RegisterGenerationStatsClient(IServiceCollection services, AgentFrameworkConfig framework)
    {
        if (framework.ClientType != AIAgentFrameworkClientType.OpenAI
            || !framework.EnablePromptCaching
            || string.IsNullOrWhiteSpace(framework.Endpoint)
            || !Uri.TryCreate(framework.Endpoint, UriKind.Absolute, out var endpointUri)
            || !endpointUri.Host.Contains("openrouter", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // BaseAddress must end in a slash so the relative "generation?id=" segment resolves under
        // the configured /api/v1 path rather than replacing it.
        var baseAddress = endpointUri.AbsoluteUri.EndsWith('/')
            ? endpointUri.AbsoluteUri
            : endpointUri.AbsoluteUri + "/";
        var apiKey = framework.ApiKey;

        services.AddHttpClient<IGenerationStatsClient, OpenRouterGenerationStatsClient>(client =>
        {
            client.BaseAddress = new Uri(baseAddress);
            client.Timeout = TimeSpan.FromSeconds(GenerationStatsTimeoutSeconds);
            if (!string.IsNullOrWhiteSpace(apiKey))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        });
    }
}

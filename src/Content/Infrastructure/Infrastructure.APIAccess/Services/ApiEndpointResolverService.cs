using Domain.Common.Config;
using Domain.Common.Config.Http;
using Domain.Common.Models.Api;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace Infrastructure.APIAccess.Services;

/// <summary>
/// Resolves API endpoints and retrieves strongly-typed HTTP client configurations
/// from <see cref="AppConfig"/>. Caches resolved endpoints to reduce repeated lookups.
/// </summary>
/// <remarks>
/// When service discovery is enabled for a client, this service health-checks all
/// configured endpoints (primary + alternatives) and selects the healthiest one.
/// Results are cached according to each client's <see cref="HttpClientConfig.CacheDuration"/>.
/// </remarks>
public sealed class ApiEndpointResolverService
{
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<ApiEndpointResolverService> _logger;
    private readonly IMemoryCache _cache;

    /// <summary>
    /// Initializes a new instance of <see cref="ApiEndpointResolverService"/>.
    /// </summary>
    /// <param name="appConfig">Monitor for accessing current application configuration.</param>
    /// <param name="logger">Logger for recording resolution events and errors.</param>
    /// <param name="cache">Memory cache for storing resolved endpoints.</param>
    public ApiEndpointResolverService(
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<ApiEndpointResolverService> logger,
        IMemoryCache cache)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(cache);

        _appConfig = appConfig;
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// Resolves the endpoint URI for a specific client configuration path.
    /// Uses service discovery when enabled; otherwise returns the configured BaseAddress.
    /// </summary>
    /// <typeparam name="TClientOptions">The HTTP client configuration type.</typeparam>
    /// <param name="configurationSectionName">The configuration path identifying the client.</param>
    /// <returns>The resolved endpoint URI.</returns>
    public Uri ResolveEndpoint<TClientOptions>(string configurationSectionName)
        where TClientOptions : HttpClientConfig, new()
    {
        var cacheKey = $"endpoint-{configurationSectionName}";

        if (_cache.TryGetValue(cacheKey, out Uri? cachedEndpoint))
        {
            _logger.LogDebug(
                "Using cached endpoint for {ConfigurationSectionName}: {Endpoint}",
                configurationSectionName, cachedEndpoint);
            return cachedEndpoint!;
        }

        var clientConfig = GetClientConfig<TClientOptions>(configurationSectionName);
        var resolvedEndpoint = ResolveEndpointFromConfig(clientConfig);

        _cache.Set(cacheKey, resolvedEndpoint, clientConfig.CacheDuration);

        _logger.LogDebug(
            "Resolved endpoint for {ConfigurationSectionName}: {Endpoint}",
            configurationSectionName, resolvedEndpoint);

        return resolvedEndpoint;
    }

    /// <summary>
    /// Retrieves the strongly-typed HTTP client configuration for a configuration section.
    /// </summary>
    /// <typeparam name="TClientOptions">The HTTP client configuration type.</typeparam>
    /// <param name="configurationSectionName">The configuration section name.</param>
    /// <returns>The strongly-typed client configuration.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the configuration section name is not recognized.
    /// Add new cases to the switch as typed clients are registered.
    /// </exception>
    public TClientOptions GetClientConfig<TClientOptions>(string configurationSectionName)
        where TClientOptions : HttpClientConfig, new()
    {
        // Add cases here as typed HTTP clients are registered.
        // Example:
        // case ApiAccessConstants.AzureOpenAiConfigurationSection:
        //     return (TClientOptions)(object)config.Azure.AzureOpenAI;
        throw new ArgumentException(
            $"Unknown configuration section name: {configurationSectionName}",
            nameof(configurationSectionName));
    }

    private Uri ResolveEndpointFromConfig(HttpClientConfig config)
    {
        if (config.EnableServiceDiscovery && config.AlternativeEndpoints.Count > 0)
        {
            return DiscoverHealthyEndpointAsync(config).GetAwaiter().GetResult();
        }

        return new Uri(config.BaseAddress);
    }

    private async Task<Uri> DiscoverHealthyEndpointAsync(HttpClientConfig config)
    {
        var allEndpoints = new List<string>(config.AlternativeEndpoints.Count + 1) { config.BaseAddress };
        allEndpoints.AddRange(config.AlternativeEndpoints);

        using var httpClient = new HttpClient { Timeout = config.HealthCheckTimeout };

        var healthCheckTasks = allEndpoints.Select(endpoint =>
            TestEndpointHealthAsync(endpoint, config.HealthCheckPath, httpClient));

        var healthResults = await Task.WhenAll(healthCheckTasks);

        var healthiestEndpoint = healthResults
            .Where(r => r.IsHealthy)
            .OrderBy(r => r.ResponseTime)
            .FirstOrDefault();

        return healthiestEndpoint?.Endpoint ?? new Uri(config.BaseAddress);
    }

    private static async Task<EndpointHealthResult> TestEndpointHealthAsync(
        string endpointUrl,
        string healthCheckPath,
        HttpClient httpClient)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Head, $"{endpointUrl}{healthCheckPath}");
            using var response = await httpClient.SendAsync(request);
            stopwatch.Stop();

            return new EndpointHealthResult
            {
                IsHealthy = response.IsSuccessStatusCode,
                Endpoint = new Uri(endpointUrl),
                ResponseTime = stopwatch.Elapsed,
            };
        }
        catch
        {
            stopwatch.Stop();

            return new EndpointHealthResult
            {
                IsHealthy = false,
                Endpoint = new Uri(endpointUrl),
                ResponseTime = stopwatch.Elapsed,
            };
        }
    }
}

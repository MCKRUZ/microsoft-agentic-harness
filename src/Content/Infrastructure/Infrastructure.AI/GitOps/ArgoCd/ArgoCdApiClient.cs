using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Domain.Common.Config;
using Infrastructure.AI.Egress;

namespace Infrastructure.AI.GitOps.ArgoCd;

/// <summary>
/// Thin HTTP client for the Argo CD API server. Reads <c>Application</c>
/// resource status via the documented REST surface
/// (<c>/api/v1/applications</c>). Uses the egress-gated named
/// <see cref="HttpClient"/> from PR-3b.
/// </summary>
/// <remarks>
/// <para>
/// Argo CD's REST API is the source of truth — this client never bypasses
/// it by reading the underlying CRDs directly. Auth is expected to be carried
/// on the egress <see cref="HttpClient"/> by the consumer's auth strategy
/// (delegating handler stamped Bearer token); the client itself does not
/// negotiate auth.
/// </para>
/// </remarks>
public sealed class ArgoCdApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<ArgoCdApiClient> _logger;

    /// <summary>Initializes a new <see cref="ArgoCdApiClient"/>.</summary>
    public ArgoCdApiClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppConfig> config,
        ILogger<ArgoCdApiClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// List Argo CD <c>Application</c> resources with their sync + health status.
    /// </summary>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    public async Task<IReadOnlyList<ArgoCdApplicationStatus>> ListApplicationsAsync(
        CancellationToken cancellationToken)
    {
        var baseUrl = _config.CurrentValue.AI.GitOps.ArgoCdApiBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                "AppConfig.AI.GitOps.ArgoCdApiBaseUrl is empty. The GitOps startup validator should have caught this.");
        }

        var client = _httpClientFactory.CreateClient(EgressPolicyDelegatingHandler.ClientName);
        var url = $"{baseUrl.TrimEnd('/')}/api/v1/applications";

        _logger.LogDebug("Listing Argo CD Applications from {Url}", url);

        var response = await client
            .GetFromJsonAsync<ArgoCdApplicationListResponse>(url, cancellationToken)
            .ConfigureAwait(false);

        return response?.Items ?? [];
    }
}

/// <summary>Argo CD Application status as returned by the API server.</summary>
public sealed record ArgoCdApplicationStatus
{
    /// <summary>The Application metadata block.</summary>
    [JsonPropertyName("metadata")]
    public ArgoCdMetadata Metadata { get; init; } = new();

    /// <summary>The Application spec block.</summary>
    [JsonPropertyName("spec")]
    public ArgoCdSpec Spec { get; init; } = new();

    /// <summary>The Application status block.</summary>
    [JsonPropertyName("status")]
    public ArgoCdStatus Status { get; init; } = new();
}

/// <summary>Argo CD Application metadata fragment.</summary>
public sealed record ArgoCdMetadata
{
    /// <summary>The Application name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The namespace (almost always "argocd").</summary>
    [JsonPropertyName("namespace")]
    public string Namespace { get; init; } = string.Empty;
}

/// <summary>Argo CD Application spec fragment.</summary>
public sealed record ArgoCdSpec
{
    /// <summary>The Git source the Application reconciles from.</summary>
    [JsonPropertyName("source")]
    public ArgoCdSource Source { get; init; } = new();
}

/// <summary>Argo CD Application source fragment.</summary>
public sealed record ArgoCdSource
{
    /// <summary>The Git repository URL.</summary>
    [JsonPropertyName("repoURL")]
    public string RepoUrl { get; init; } = string.Empty;

    /// <summary>The path within the repo.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;
}

/// <summary>Argo CD Application status fragment.</summary>
public sealed record ArgoCdStatus
{
    /// <summary>The sync status block.</summary>
    [JsonPropertyName("sync")]
    public ArgoCdSyncStatus Sync { get; init; } = new();

    /// <summary>The health status block.</summary>
    [JsonPropertyName("health")]
    public ArgoCdHealthStatus Health { get; init; } = new();
}

/// <summary>Argo CD sync status fragment.</summary>
public sealed record ArgoCdSyncStatus
{
    /// <summary>The sync status string — <c>Synced</c>, <c>OutOfSync</c>, or <c>Unknown</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>The last-synced revision.</summary>
    [JsonPropertyName("revision")]
    public string Revision { get; init; } = string.Empty;
}

/// <summary>Argo CD health status fragment.</summary>
public sealed record ArgoCdHealthStatus
{
    /// <summary>The health status string — <c>Healthy</c>, <c>Progressing</c>, <c>Degraded</c>, <c>Suspended</c>, <c>Missing</c>, or <c>Unknown</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>Human-readable message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

internal sealed record ArgoCdApplicationListResponse
{
    [JsonPropertyName("items")]
    public IReadOnlyList<ArgoCdApplicationStatus> Items { get; init; } = [];
}

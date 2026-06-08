using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Domain.Common.Config;
using Infrastructure.AI.Egress;

namespace Infrastructure.AI.GitOps.Flux;

/// <summary>
/// Thin HTTP client for Flux v2 controller status surfaces. Talks to a Flux
/// instance running the source-controller + kustomize-controller + helm-controller
/// stack, reading <c>Kustomization</c> and <c>HelmRelease</c> reconciliation
/// status. Uses the egress-gated named <see cref="HttpClient"/> from PR-3b so
/// allowlist + SSRF defense apply to every outbound call.
/// </summary>
/// <remarks>
/// <para>
/// Flux does not ship a single uniform HTTP API the way Argo CD does. Real
/// production setups expose Flux status via the cluster's Kubernetes API
/// (read via service-account token) or via a metrics + status sidecar. This
/// client targets the documented HTTP status surface for portability; the
/// concrete URL shape is config-driven via
/// <c>AppConfig.AI.GitOps.FluxApiBaseUrl</c>.
/// </para>
/// <para>
/// The client is intentionally minimal — it returns raw status DTOs which
/// <see cref="FluxGitOpsController"/> maps onto the controller-neutral
/// <c>DriftReport</c> / <c>ClusterHealth</c> shapes. Keeping the mapping
/// outside this class makes the wire types replaceable without touching the
/// controller logic.
/// </para>
/// </remarks>
public sealed class FluxApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<FluxApiClient> _logger;

    /// <summary>Initializes a new <see cref="FluxApiClient"/>.</summary>
    public FluxApiClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppConfig> config,
        ILogger<FluxApiClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// List the <c>Kustomization</c> resources the Flux controller is tracking,
    /// with their reconciliation status.
    /// </summary>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    public async Task<IReadOnlyList<FluxKustomizationStatus>> ListKustomizationsAsync(
        CancellationToken cancellationToken)
    {
        var baseUrl = _config.CurrentValue.AI.GitOps.FluxApiBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                "AppConfig.AI.GitOps.FluxApiBaseUrl is empty. The GitOps startup validator should have caught this.");
        }

        var client = _httpClientFactory.CreateClient(EgressPolicyDelegatingHandler.ClientName);
        var url = $"{baseUrl.TrimEnd('/')}/api/v1/kustomizations";

        _logger.LogDebug("Listing Flux Kustomizations from {Url}", url);

        var response = await client
            .GetFromJsonAsync<FluxKustomizationListResponse>(url, cancellationToken)
            .ConfigureAwait(false);

        return response?.Items ?? [];
    }

    /// <summary>
    /// List the <c>HelmRelease</c> resources the Flux controller is tracking,
    /// with their reconciliation status.
    /// </summary>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    public async Task<IReadOnlyList<FluxHelmReleaseStatus>> ListHelmReleasesAsync(
        CancellationToken cancellationToken)
    {
        var baseUrl = _config.CurrentValue.AI.GitOps.FluxApiBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                "AppConfig.AI.GitOps.FluxApiBaseUrl is empty. The GitOps startup validator should have caught this.");
        }

        var client = _httpClientFactory.CreateClient(EgressPolicyDelegatingHandler.ClientName);
        var url = $"{baseUrl.TrimEnd('/')}/api/v1/helmreleases";

        _logger.LogDebug("Listing Flux HelmReleases from {Url}", url);

        var response = await client
            .GetFromJsonAsync<FluxHelmReleaseListResponse>(url, cancellationToken)
            .ConfigureAwait(false);

        return response?.Items ?? [];
    }
}

/// <summary>Flux v2 Kustomization status as returned by the controller's HTTP status surface.</summary>
public sealed record FluxKustomizationStatus
{
    /// <summary>The Kustomization name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The namespace the Kustomization lives in.</summary>
    [JsonPropertyName("namespace")]
    public string Namespace { get; init; } = string.Empty;

    /// <summary>The Git path the Kustomization reconciles from.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    /// <summary>True when the Kustomization is Ready.</summary>
    [JsonPropertyName("ready")]
    public bool Ready { get; init; }

    /// <summary>True when the Kustomization is currently suspended (paused).</summary>
    [JsonPropertyName("suspended")]
    public bool Suspended { get; init; }

    /// <summary>Last-reconciled commit SHA.</summary>
    [JsonPropertyName("lastAppliedRevision")]
    public string LastAppliedRevision { get; init; } = string.Empty;

    /// <summary>Source commit SHA Git currently has.</summary>
    [JsonPropertyName("lastAttemptedRevision")]
    public string LastAttemptedRevision { get; init; } = string.Empty;

    /// <summary>Human-readable status message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

/// <summary>Flux v2 HelmRelease status as returned by the controller's HTTP status surface.</summary>
public sealed record FluxHelmReleaseStatus
{
    /// <summary>The HelmRelease name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The namespace the HelmRelease lives in.</summary>
    [JsonPropertyName("namespace")]
    public string Namespace { get; init; } = string.Empty;

    /// <summary>True when the release reconciled successfully.</summary>
    [JsonPropertyName("ready")]
    public bool Ready { get; init; }

    /// <summary>True when reconciliation is suspended.</summary>
    [JsonPropertyName("suspended")]
    public bool Suspended { get; init; }

    /// <summary>Human-readable status message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

internal sealed record FluxKustomizationListResponse
{
    [JsonPropertyName("items")]
    public IReadOnlyList<FluxKustomizationStatus> Items { get; init; } = [];
}

internal sealed record FluxHelmReleaseListResponse
{
    [JsonPropertyName("items")]
    public IReadOnlyList<FluxHelmReleaseStatus> Items { get; init; } = [];
}

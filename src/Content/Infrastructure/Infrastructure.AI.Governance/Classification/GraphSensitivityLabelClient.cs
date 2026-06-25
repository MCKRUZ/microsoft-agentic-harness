using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Governance;
using Azure.Core;
using Domain.AI.Governance;
using Domain.Common.Config.AI.Governance;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Governance.Classification;

/// <summary>
/// Microsoft Purview Information Protection (MIP) classification provider, backed by Microsoft Graph.
/// Resolves an asset's sensitivity by mapping the label id embedded in the asset reference against the
/// organization's label taxonomy, which it fetches and caches from Graph.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What Graph can and cannot do.</strong> Graph supplies the tenant's label dictionary
/// (<c>GET /security/dataSecurityAndGovernance/sensitivityLabels</c>, application permission
/// <c>SensitivityLabels.Read.All</c>) — the id → name mapping. It does <em>not</em> open a file and read
/// the label stamped inside it; that extraction requires the native MIP File SDK and is the asset
/// resolver's responsibility. This client therefore resolves a real label only when the asset's
/// identifier already carries an embedded label id (a bare GUID, or the MIP
/// <c>MSIP_Label_{guid}_Enabled</c> marker). An ordinary local file path carries no such id and resolves
/// to <see cref="AssetLabelResult.Unknown(AssetReference, DateTimeOffset)"/> — the documented common case.
/// </para>
/// <para>
/// The taxonomy is cached in-process with a configurable TTL so the gate does not pay a Graph round trip
/// per call. Per-asset results are cached separately by <see cref="CachedDataClassificationProvider"/>.
/// A failure to reach Graph for an asset that <em>does</em> carry a label id surfaces as an exception
/// rather than a benign Unknown, so the enforcement point can apply its fail-closed policy instead of
/// silently treating an unreachable backend as "nothing to see".
/// </para>
/// </remarks>
public sealed partial class GraphSensitivityLabelClient : IDataClassificationProvider, IDisposable
{
    private const string LabelsPath = "security/dataSecurityAndGovernance/sensitivityLabels";

    /// <summary>
    /// Hard cap on pagination follow-through, a guard against a malformed or looping
    /// <c>@odata.nextLink</c> chain. A real tenant label taxonomy is far below this.
    /// </summary>
    private const int MaxCatalogPages = 100;

    /// <summary>
    /// Name of the <see cref="IHttpClientFactory"/> client this provider resolves on each fetch. Using
    /// the factory per call lets it pool and rotate the underlying handler instead of pinning one handler
    /// for the lifetime of this singleton provider (which would leave DNS and sockets stale).
    /// </summary>
    public const string HttpClientName = "purview-information-protection";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TokenCredential _credential;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GraphSensitivityLabelClient> _logger;

    private readonly Uri _labelsEndpoint;
    private readonly string[] _scopes;
    private readonly TimeSpan _catalogTtl;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile CatalogSnapshot? _catalog;

    /// <summary>Initializes a new instance of the <see cref="GraphSensitivityLabelClient"/> class.</summary>
    /// <param name="httpClientFactory">Factory used to resolve a fresh, handler-rotated client per Graph fetch.</param>
    /// <param name="credential">Entra credential used to mint Graph access tokens.</param>
    /// <param name="config">The Information Protection provider configuration this client binds to.</param>
    /// <param name="timeProvider">Clock used for cache expiry and result timestamps.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public GraphSensitivityLabelClient(
        IHttpClientFactory httpClientFactory,
        TokenCredential credential,
        InformationProtectionProviderConfig config,
        TimeProvider timeProvider,
        ILogger<GraphSensitivityLabelClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        var ip = config;
        _httpClientFactory = httpClientFactory;
        _credential = credential;
        _timeProvider = timeProvider;
        _logger = logger;
        _scopes = [.. ip.Scopes];
        _catalogTtl = ip.LabelCatalogCacheTtl;
        _labelsEndpoint = BuildLabelsEndpoint(ip.GraphBaseUrl);
    }

    /// <inheritdoc />
    public async Task<AssetLabelResult> GetLabelAsync(AssetReference asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        // No embedded label id means there is nothing for Graph to map — return Unknown without a network
        // call. This is the common case for ordinary files and keeps the gate cheap when nothing is labelled.
        if (!TryExtractLabelId(asset.Identifier, out var labelId))
            return AssetLabelResult.Unknown(asset, _timeProvider.GetUtcNow());

        var catalog = await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        if (catalog.ById.TryGetValue(labelId, out var label))
            return new AssetLabelResult(asset, label, [], LabelSource.InformationProtection, now);

        // The asset carries a label id the tenant taxonomy does not contain (a label deleted since the
        // file was stamped, or a different tenant). Treat as Unknown so the unknown-asset policy applies.
        _logger.LogWarning(
            "Embedded sensitivity label id {LabelId} is not present in the tenant label taxonomy; treating asset as unclassified.",
            labelId);
        return AssetLabelResult.Unknown(asset, now);
    }

    private async Task<CatalogSnapshot> EnsureCatalogAsync(CancellationToken cancellationToken)
    {
        var snapshot = _catalog;
        if (snapshot is not null && _timeProvider.GetUtcNow() < snapshot.ExpiresAt)
            return snapshot;

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = _catalog;
            if (snapshot is not null && _timeProvider.GetUtcNow() < snapshot.ExpiresAt)
                return snapshot;

            var labels = await FetchCatalogAsync(cancellationToken).ConfigureAwait(false);
            snapshot = new CatalogSnapshot(labels, _timeProvider.GetUtcNow() + _catalogTtl);
            _catalog = snapshot;
            return snapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, SensitivityLabel>> FetchCatalogAsync(CancellationToken cancellationToken)
    {
        var labels = new Dictionary<string, SensitivityLabel>(StringComparer.OrdinalIgnoreCase);

        // Follow @odata.nextLink so a paginated taxonomy is read in full rather than silently truncated
        // to the first page (which would degrade later labels to Unknown).
        Uri? nextUrl = _labelsEndpoint;
        var pages = 0;
        while (nextUrl is not null)
        {
            if (++pages > MaxCatalogPages)
            {
                throw new InvalidOperationException(
                    $"The Microsoft Graph sensitivity-label taxonomy exceeded the {MaxCatalogPages}-page cap; refusing to follow further pagination.");
            }

            var page = await GetPageAsync(nextUrl, cancellationToken).ConfigureAwait(false);
            foreach (var dto in page.Value!)
            {
                if (string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Name))
                    continue;

                labels[NormalizeLabelId(dto.Id)] = new SensitivityLabel(dto.Id, dto.Name);
            }

            nextUrl = ResolveNextLink(page.NextLink);
        }

        _logger.LogInformation(
            "Loaded {LabelCount} Purview sensitivity labels from Graph across {PageCount} page(s).", labels.Count, pages);
        return labels;
    }

    private async Task<GraphLabelListResponse> GetPageAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var token = await _credential
            .GetTokenAsync(new TokenRequestContext(_scopes), cancellationToken)
            .ConfigureAwait(false);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

        // Resolved (and disposed) per request so the factory can rotate the underlying handler.
        using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Status only — never echo the response body or token, which can carry tenant detail.
            throw new InvalidOperationException(
                $"Failed to retrieve the Microsoft Purview sensitivity-label taxonomy from Graph (HTTP {(int)response.StatusCode}).");
        }

        var payload = await response.Content
            .ReadFromJsonAsync<GraphLabelListResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        // A well-formed Graph collection always carries a 'value' array — empty is legitimate (a tenant
        // with no labels), but a missing 'value' means the response shape was not understood. Treat that
        // as a failure rather than silently caching an empty taxonomy that would degrade labelled assets
        // to Unknown for the whole TTL.
        if (payload?.Value is null)
        {
            throw new InvalidOperationException(
                "The Microsoft Graph sensitivity-label response did not contain a 'value' collection; the response shape was not understood.");
        }

        return payload;
    }

    private static Uri BuildLabelsEndpoint(string graphBaseUrl)
    {
        var baseUrl = graphBaseUrl.EndsWith('/') ? graphBaseUrl : graphBaseUrl + "/";
        return new Uri(new Uri(baseUrl), LabelsPath);
    }

    /// <summary>
    /// Parses and validates an <c>@odata.nextLink</c>, returning the next page URL or null on the final
    /// page. The link comes from the response body, and each page request carries the access token, so the
    /// scheme and authority (host and port) must match the configured Graph endpoint — otherwise a
    /// tampered or misrouted response could trick the client into forwarding the bearer token to an
    /// attacker-controlled host, a downgraded scheme, or a different port.
    /// </summary>
    private Uri? ResolveNextLink(string? nextLink)
    {
        if (string.IsNullOrWhiteSpace(nextLink))
            return null;

        if (!Uri.TryCreate(nextLink, UriKind.Absolute, out var next) ||
            next.Scheme != _labelsEndpoint.Scheme ||
            !string.Equals(next.Authority, _labelsEndpoint.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The Microsoft Graph @odata.nextLink pointed to an unexpected or malformed endpoint; refusing " +
                "to forward the access token off the configured Graph scheme/host/port.");
        }

        return next;
    }

    /// <summary>
    /// Extracts an embedded sensitivity-label id from an asset identifier. Recognizes the MIP
    /// <c>MSIP_Label_{guid}_…</c> marker and a bare GUID. Returns the id normalized to its canonical form.
    /// </summary>
    private static bool TryExtractLabelId(string identifier, out string labelId)
    {
        labelId = string.Empty;
        if (string.IsNullOrWhiteSpace(identifier))
            return false;

        var marker = MsipLabelMarker().Match(identifier);
        if (marker.Success)
        {
            labelId = NormalizeLabelId(marker.Groups["id"].Value);
            return true;
        }

        if (Guid.TryParse(identifier.Trim(), out var guid))
        {
            labelId = guid.ToString("D");
            return true;
        }

        return false;
    }

    private static string NormalizeLabelId(string id) =>
        Guid.TryParse(id, out var guid) ? guid.ToString("D") : id.Trim();

    [GeneratedRegex(@"MSIP_Label_(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})_",
        RegexOptions.IgnoreCase)]
    private static partial Regex MsipLabelMarker();

    /// <summary>Disposes the catalog-refresh lock.</summary>
    public void Dispose() => _refreshLock.Dispose();

    /// <summary>An immutable snapshot of the label taxonomy with its expiry.</summary>
    private sealed record CatalogSnapshot(
        IReadOnlyDictionary<string, SensitivityLabel> ById,
        DateTimeOffset ExpiresAt);
}

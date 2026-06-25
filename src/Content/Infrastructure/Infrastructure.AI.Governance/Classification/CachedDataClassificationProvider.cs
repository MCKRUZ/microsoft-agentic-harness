using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Governance.Classification;

/// <summary>
/// Time-to-live caching decorator over an <see cref="IDataClassificationProvider"/>. Memoizes resolved
/// <see cref="AssetLabelResult"/>s keyed by the <see cref="AssetReference"/> so repeated tool calls
/// against the same asset within the TTL window do not each incur a Purview round trip.
/// </summary>
/// <remarks>
/// <para>
/// Sensitivity labels change on the organization's scan/edit cadence, not per request, so a short result
/// cache is safe and is what makes per-invocation gating affordable. The TTL is configured by
/// <c>DataClassificationConfig.ResultCacheTtl</c>; a non-positive TTL disables caching and every call
/// passes straight through to the inner provider.
/// </para>
/// <para>
/// Only successful resolutions are cached. If the inner provider throws (for example, the backend is
/// unreachable for an asset that carries a label id), the exception propagates and nothing is stored, so
/// a transient failure is never frozen into the cache.
/// </para>
/// </remarks>
public sealed class CachedDataClassificationProvider : IDataClassificationProvider
{
    private readonly IDataClassificationProvider _inner;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private readonly ILogger<CachedDataClassificationProvider> _logger;
    private readonly ConcurrentDictionary<AssetReference, CacheEntry> _cache = new();

    /// <summary>Initializes a new instance of the <see cref="CachedDataClassificationProvider"/> class.</summary>
    /// <param name="inner">The provider whose results are cached.</param>
    /// <param name="timeProvider">Clock used to evaluate entry expiry.</param>
    /// <param name="ttl">How long a resolved result is reused; non-positive disables caching.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public CachedDataClassificationProvider(
        IDataClassificationProvider inner,
        TimeProvider timeProvider,
        TimeSpan ttl,
        ILogger<CachedDataClassificationProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _timeProvider = timeProvider;
        _ttl = ttl;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AssetLabelResult> GetLabelAsync(AssetReference asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (_ttl <= TimeSpan.Zero)
            return await _inner.GetLabelAsync(asset, cancellationToken).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(asset, out var entry) && now < entry.ExpiresAt)
            return entry.Result;

        var result = await _inner.GetLabelAsync(asset, cancellationToken).ConfigureAwait(false);
        _cache[asset] = new CacheEntry(result, now + _ttl);
        return result;
    }

    private sealed record CacheEntry(AssetLabelResult Result, DateTimeOffset ExpiresAt);
}

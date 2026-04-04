using Microsoft.Extensions.Caching.Hybrid;

namespace Application.Common.Helpers;

/// <summary>
/// Factory methods for creating <see cref="HybridCacheEntryOptions"/> with sensible defaults.
/// Consumed by <c>ICacheableQuery</c> implementations and infrastructure cache configuration.
/// </summary>
/// <remarks>
/// Centralizes cache policy creation so consumers don't need to know the default
/// expiration values or flag combinations. All methods return immutable options objects.
/// </remarks>
public static class CacheOptionsHelper
{
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultLocalCacheExpiration = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Creates <see cref="HybridCacheEntryOptions"/> with configurable expiration and flags.
    /// </summary>
    /// <param name="expiration">
    /// Absolute expiration relative to now. Default: 5 minutes.
    /// </param>
    /// <param name="localCacheExpiration">
    /// L1 (in-memory) cache expiration. Default: 2 minutes.
    /// Should be shorter than <paramref name="expiration"/> to ensure freshness.
    /// </param>
    /// <param name="flags">Cache entry flags. Default: <see cref="HybridCacheEntryFlags.None"/>.</param>
    /// <returns>A configured <see cref="HybridCacheEntryOptions"/> instance.</returns>
    /// <example>
    /// <code>
    /// // Default options (5min absolute, 2min local)
    /// var options = CacheOptionsHelper.GetHybridCacheOptions();
    ///
    /// // Custom: 30min with local disabled
    /// var longLived = CacheOptionsHelper.GetHybridCacheOptions(
    ///     expiration: TimeSpan.FromMinutes(30),
    ///     flags: HybridCacheEntryFlags.DisableLocalCache);
    /// </code>
    /// </example>
    public static HybridCacheEntryOptions GetHybridCacheOptions(
        TimeSpan? expiration = null,
        TimeSpan? localCacheExpiration = null,
        HybridCacheEntryFlags flags = HybridCacheEntryFlags.None)
    {
        return new HybridCacheEntryOptions
        {
            Expiration = expiration ?? DefaultExpiration,
            LocalCacheExpiration = localCacheExpiration ?? DefaultLocalCacheExpiration,
            Flags = flags
        };
    }

    /// <summary>
    /// Creates short-lived cache options suitable for frequently-changing data
    /// like agent state or tool availability.
    /// </summary>
    /// <returns>Options with 30-second absolute and 15-second local expiration.</returns>
    public static HybridCacheEntryOptions GetShortLivedOptions() =>
        GetHybridCacheOptions(
            expiration: TimeSpan.FromSeconds(30),
            localCacheExpiration: TimeSpan.FromSeconds(15));

    /// <summary>
    /// Creates long-lived cache options suitable for rarely-changing data
    /// like parsed agent manifests or skill definitions.
    /// </summary>
    /// <returns>Options with 1-hour absolute and 30-minute local expiration.</returns>
    public static HybridCacheEntryOptions GetLongLivedOptions() =>
        GetHybridCacheOptions(
            expiration: TimeSpan.FromHours(1),
            localCacheExpiration: TimeSpan.FromMinutes(30));
}

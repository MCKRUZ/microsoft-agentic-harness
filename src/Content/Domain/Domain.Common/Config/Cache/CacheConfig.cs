namespace Domain.Common.Config.Cache;

/// <summary>
/// Configuration for the caching strategy and backing store.
/// </summary>
/// <remarks>
/// <para>
/// Supported cache types:
/// <list type="bullet">
///   <item><c>None</c> — No caching (default for POC)</item>
///   <item><c>Memory</c> — In-memory cache with distributed memory fallback</item>
///   <item><c>DistributedMemory</c> — Distributed memory cache only</item>
///   <item><c>RedisCache</c> — Redis via StackExchange.Redis</item>
/// </list>
/// </para>
/// <para>
/// Redis configuration is only used when <see cref="CacheType"/> is <c>RedisCache</c>.
/// </para>
/// </remarks>
public class CacheConfig
{
    /// <summary>
    /// Gets or sets the cache strategy to use.
    /// </summary>
    /// <value>Default: <see cref="Cache.CacheType.None"/>.</value>
    public CacheType CacheType { get; set; } = CacheType.None;

    /// <summary>
    /// Gets or sets the Redis client configuration.
    /// Only used when <see cref="CacheType"/> is <see cref="Cache.CacheType.RedisCache"/>.
    /// </summary>
    public RedisClientConfig RedisClient { get; set; } = new();
}

/// <summary>
/// Redis client connection configuration.
/// </summary>
public class RedisClientConfig
{
    /// <summary>
    /// Gets or sets the Redis server endpoint (host:port).
    /// When empty, Redis health checks are disabled.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Redis authentication secret.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>WARNING:</strong> This value must NEVER be stored in appsettings.json or any file
    /// committed to source control. Use User Secrets (development) or Azure Key Vault (production).
    /// </para>
    /// </remarks>
    public string Secret { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Redis service name (for Sentinel configurations).
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Redis client identifier.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Redis collection/database name.
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;
}

/// <summary>
/// Supported caching strategies for the application.
/// </summary>
public enum CacheType
{
    /// <summary>No caching configured.</summary>
    None,

    /// <summary>In-memory cache with distributed memory cache fallback for IDistributedCache consumers.</summary>
    Memory,

    /// <summary>Distributed memory cache only (in-memory IDistributedCache implementation).</summary>
    DistributedMemory,

    /// <summary>Redis cache via StackExchange.Redis.</summary>
    RedisCache
}

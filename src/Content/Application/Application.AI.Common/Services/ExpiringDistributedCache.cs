using Domain.Common.Compliance;
using Microsoft.Extensions.Caching.Distributed;

namespace Application.AI.Common.Services;

/// <summary>
/// Wraps an <see cref="IDistributedCache"/> so nothing written through it outlives a maximum lifetime (#901).
/// </summary>
/// <remarks>
/// Used for the chat response cache. Microsoft.Extensions.AI's <c>DistributedCachingChatClient</c> writes entries with no
/// expiry and offers no setting for one, so a Redis-backed cache kept every model response for good. Every write here
/// gets an absolute expiry of at most <see cref="MaxLifetime"/>: an entry with none is given it, and one with a later
/// expiry is capped. A sliding window is passed through, because the absolute expiry ends the entry however often it is
/// read. Reads, refreshes and removals pass straight through.
/// </remarks>
[PersonalDataChannel("Writes cache entries for its callers with a bounded lifetime; the chat response cache (AgentFactory) describes what it holds.")]
public sealed class ExpiringDistributedCache : IDistributedCache
{
    private readonly IDistributedCache _inner;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="ExpiringDistributedCache"/> class.</summary>
    /// <param name="inner">The cache entries are stored in.</param>
    /// <param name="maxLifetime">The longest any entry is kept; must be positive.</param>
    /// <param name="timeProvider">The clock an absolute expiry is compared against.</param>
    public ExpiringDistributedCache(IDistributedCache inner, TimeSpan maxLifetime, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxLifetime, TimeSpan.Zero);

        _inner = inner;
        MaxLifetime = maxLifetime;
        _timeProvider = timeProvider;
    }

    /// <summary>The longest any entry written through this cache is kept.</summary>
    public TimeSpan MaxLifetime { get; }

    /// <inheritdoc />
    public byte[]? Get(string key) => _inner.Get(key);

    /// <inheritdoc />
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => _inner.GetAsync(key, token);

    /// <inheritdoc />
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _inner.Set(key, value, Bounded(options));

    /// <inheritdoc />
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
        _inner.SetAsync(key, value, Bounded(options), token);

    /// <inheritdoc />
    public void Refresh(string key) => _inner.Refresh(key);

    /// <inheritdoc />
    public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);

    /// <inheritdoc />
    public void Remove(string key) => _inner.Remove(key);

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);

    /// <summary>The caller's options with an absolute expiry no later than <see cref="MaxLifetime"/> from now.</summary>
    private DistributedCacheEntryOptions Bounded(DistributedCacheEntryOptions? options)
    {
        var latest = _timeProvider.GetUtcNow() + MaxLifetime;
        var requested = options?.AbsoluteExpiration
            ?? (options?.AbsoluteExpirationRelativeToNow is { } relative ? _timeProvider.GetUtcNow() + relative : null);

        return new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = requested is { } at && at < latest ? at : latest,
            // Kept as asked: the absolute expiry above bounds the entry whatever the window.
            SlidingExpiration = options?.SlidingExpiration,
        };
    }
}

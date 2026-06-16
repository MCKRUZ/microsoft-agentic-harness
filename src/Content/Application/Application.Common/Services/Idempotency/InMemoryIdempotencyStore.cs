using System.Collections.Concurrent;
using Application.Common.Interfaces.Idempotency;

namespace Application.Common.Services.Idempotency;

/// <summary>
/// In-process, TTL-bounded implementation of <see cref="IIdempotencyStore"/>.
/// Caches response objects by reference so the exact runtime type is preserved
/// on retrieval (no serialization round-trip), which the
/// <see cref="MediatRBehaviors.IdempotencyBehavior{TRequest, TResponse}"/> relies on
/// for its <c>cached is TResponse</c> type check.
/// </summary>
/// <remarks>
/// <para>
/// This is the default implementation registered by
/// <c>AddApplicationCommonDependencies</c>. It is single-process: cached responses do
/// not survive a restart and are not shared across instances. Consumers running multiple
/// replicas should replace this registration with a distributed implementation
/// (Redis, database) that serializes responses and shares them across nodes.
/// </para>
/// <para>
/// Entries expire after <see cref="DefaultTtl"/>. Expired entries are removed lazily on
/// access; there is no background sweep, so a key written and never read again retains its
/// slot until the next <see cref="TryGetAsync"/> or <see cref="SetAsync"/> on the same key.
/// </para>
/// </remarks>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    /// <summary>The fixed time-to-live applied to every cached response.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryIdempotencyStore"/> class.
    /// </summary>
    /// <param name="timeProvider">Time abstraction used to compute and check entry expiry.</param>
    public InMemoryIdempotencyStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task<object?> TryGetAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        if (!_entries.TryGetValue(idempotencyKey, out var entry))
            return Task.FromResult<object?>(null);

        if (_timeProvider.GetUtcNow() >= entry.ExpiresAt)
        {
            // Lazy eviction — remove only if the expired entry we read is still the current one,
            // so we don't clobber a fresh write that raced in after our read. ConcurrentDictionary's
            // ICollection<KeyValuePair> implementation performs this compare-and-remove atomically.
            ((ICollection<KeyValuePair<string, CacheEntry>>)_entries).Remove(
                new KeyValuePair<string, CacheEntry>(idempotencyKey, entry));
            return Task.FromResult<object?>(null);
        }

        return Task.FromResult<object?>(entry.Response);
    }

    /// <inheritdoc />
    public Task SetAsync(string idempotencyKey, object response, CancellationToken cancellationToken)
    {
        var expiresAt = _timeProvider.GetUtcNow() + DefaultTtl;
        _entries[idempotencyKey] = new CacheEntry(response, expiresAt);
        return Task.CompletedTask;
    }

    private readonly record struct CacheEntry(object Response, DateTimeOffset ExpiresAt);
}

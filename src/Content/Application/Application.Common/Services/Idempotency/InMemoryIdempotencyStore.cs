using System.Collections.Concurrent;
using Application.Common.Interfaces.Idempotency;

namespace Application.Common.Services.Idempotency;

/// <summary>
/// In-process, TTL-bounded implementation of <see cref="IIdempotencyStore"/>.
/// Caches response objects by reference so the exact runtime type is preserved
/// on retrieval (no serialization round-trip), which the
/// <see cref="MediatRBehaviors.IdempotencyBehavior{TRequest, TResponse}"/> relies on
/// when it casts the returned response back to <c>TResponse</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the default implementation registered by
/// <c>AddApplicationCommonDependencies</c>. It is single-process: cached responses do
/// not survive a restart and are not shared across instances. Consumers running multiple
/// replicas should replace this registration with a distributed implementation
/// (Redis, database) that serializes responses and shares them across nodes — and that
/// preserves the atomic in-flight reservation guarantee documented on
/// <see cref="IIdempotencyStore"/>.
/// </para>
/// <para>
/// Concurrent callers with the same key are coalesced onto a single
/// <see cref="Lazy{T}"/>-guarded execution, so the handler runs exactly once even under a
/// race. Successful executions are persisted for <see cref="DefaultTtl"/>; entries expire
/// lazily on the next access to the same key (there is no background sweep).
/// </para>
/// </remarks>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    /// <summary>The fixed time-to-live applied to every cached response.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inFlight = new(StringComparer.Ordinal);
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
    public async Task<object> GetOrExecuteAsync(
        string scopedKey,
        Func<Task<object>> execute,
        Func<object, bool> shouldPersist,
        CancellationToken cancellationToken)
    {
        if (TryGetPersisted(scopedKey, out var persisted))
            return persisted;

        // Atomic reservation: the first caller installs the Lazy; concurrent callers get the
        // same instance. Lazy's default ExecutionAndPublication mode ensures the factory runs
        // once, so all racing callers await a single execution of the handler.
        var lazy = _inFlight.GetOrAdd(scopedKey, static (_, factory) => new Lazy<Task<object>>(factory), execute);
        try
        {
            var response = await lazy.Value.ConfigureAwait(false);
            if (shouldPersist(response))
                _entries[scopedKey] = new CacheEntry(response, _timeProvider.GetUtcNow() + DefaultTtl);
            return response;
        }
        finally
        {
            // Release the reservation once complete (compare-and-remove so a fresh reservation that
            // raced in after a throw is not clobbered). On a persisted success later callers hit the
            // fast path above; on a non-persisted or faulted response a later retry re-executes.
            ((ICollection<KeyValuePair<string, Lazy<Task<object>>>>)_inFlight).Remove(
                new KeyValuePair<string, Lazy<Task<object>>>(scopedKey, lazy));
        }
    }

    private bool TryGetPersisted(string scopedKey, out object response)
    {
        response = null!;
        if (!_entries.TryGetValue(scopedKey, out var entry))
            return false;

        if (_timeProvider.GetUtcNow() >= entry.ExpiresAt)
        {
            // Lazy eviction — remove only if the expired entry we read is still the current one,
            // so we don't clobber a fresh write that raced in after our read. ConcurrentDictionary's
            // ICollection<KeyValuePair> implementation performs this compare-and-remove atomically.
            ((ICollection<KeyValuePair<string, CacheEntry>>)_entries).Remove(
                new KeyValuePair<string, CacheEntry>(scopedKey, entry));
            return false;
        }

        response = entry.Response;
        return true;
    }

    private readonly record struct CacheEntry(object Response, DateTimeOffset ExpiresAt);
}

namespace Application.Common.Interfaces.Idempotency;

/// <summary>
/// Stores and retrieves idempotency keys to detect duplicate request executions.
/// Implementations may use in-memory, Redis, or database-backed storage.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Attempts to retrieve a cached response for the given idempotency key.
    /// Returns <see langword="null"/> if the key has not been seen before.
    /// </summary>
    /// <param name="idempotencyKey">The unique key identifying the original request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached response, or <see langword="null"/> on a cache miss.</returns>
    Task<object?> TryGetAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a response under the given idempotency key for future deduplication.
    /// The TTL is implementation-defined.
    /// </summary>
    /// <param name="idempotencyKey">The unique key identifying the original request.</param>
    /// <param name="response">The response to cache.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync(string idempotencyKey, object response, CancellationToken cancellationToken);
}

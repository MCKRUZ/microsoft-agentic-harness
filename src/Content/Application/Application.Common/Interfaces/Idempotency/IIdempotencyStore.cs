namespace Application.Common.Interfaces.Idempotency;

/// <summary>
/// Deduplicates request executions by caching responses under a caller-scoped key, and
/// guarantees that concurrent duplicates run the underlying work exactly once.
/// Implementations may use in-memory, Redis, or database-backed storage.
/// </summary>
/// <remarks>
/// <para>
/// The single <see cref="GetOrExecuteAsync"/> operation replaces the previous
/// check-then-act (<c>TryGet</c> + <c>Set</c>) pair, which had a time-of-check/time-of-use
/// race: two concurrent callers with the same key both observed a miss and both ran the
/// handler. A conforming implementation MUST provide an atomic in-flight reservation so a
/// second concurrent caller awaits the first caller's result instead of re-executing.
/// </para>
/// <para>
/// <strong>Distributed implementations</strong> (Redis, database) MUST preserve the same
/// reservation guarantee — for example with an atomic <c>SET key value NX</c> reservation
/// marker plus a wait/poll for the reserving caller's result, or a row-level lock. Providing
/// only last-write-wins caching without a reservation re-opens the double-execution defect.
/// </para>
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    /// Returns the cached response for <paramref name="scopedKey"/> when a valid (unexpired)
    /// entry exists; otherwise runs <paramref name="execute"/> exactly once — even when
    /// multiple callers race on the same key — and, when <paramref name="shouldPersist"/>
    /// returns <see langword="true"/> for the produced response, caches it for future calls.
    /// </summary>
    /// <param name="scopedKey">
    /// The fully-qualified, caller-scoped cache key. The behavior composes this from the
    /// current user identity, request type, and the caller-supplied idempotency key so that
    /// one user's key can never resolve another user's cached response.
    /// </param>
    /// <param name="execute">
    /// The work to run on a cache miss. Invoked at most once per key across concurrent callers.
    /// </param>
    /// <param name="shouldPersist">
    /// Predicate deciding whether the produced response is eligible for caching. Failure
    /// responses return <see langword="false"/> so a later retry re-executes rather than
    /// replaying the failure. Not consulted on a cache hit, nor when <paramref name="execute"/> throws.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached response on a hit, or the freshly executed response on a miss.</returns>
    Task<object> GetOrExecuteAsync(
        string scopedKey,
        Func<Task<object>> execute,
        Func<object, bool> shouldPersist,
        CancellationToken cancellationToken);
}

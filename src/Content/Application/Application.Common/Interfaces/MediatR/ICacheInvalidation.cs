namespace Application.Common.Interfaces.MediatR;

/// <summary>
/// Marker interface for commands that should invalidate cache entries on success.
/// Consumed by <c>CachingBehavior</c> to remove stale entries after the handler executes.
/// </summary>
public interface ICacheInvalidation
{
    /// <summary>Gets the cache keys to invalidate after successful command execution.</summary>
    IReadOnlyList<string> CacheKeysToInvalidate { get; }
}

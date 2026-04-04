using MediatR;
using Microsoft.Extensions.Caching.Hybrid;

namespace Application.Common.Interfaces.MediatR;

/// <summary>
/// Marker interface for queries whose responses can be cached.
/// Consumed by <c>CachingBehavior</c> to check/populate the <see cref="HybridCache"/>
/// before executing the handler.
/// </summary>
/// <typeparam name="TResponse">The query response type.</typeparam>
public interface ICacheableQuery<TResponse> : IRequest<TResponse>
{
    /// <summary>Gets the unique cache key for this query.</summary>
    string CacheKey { get; }

    /// <summary>Gets optional cache entry options. Null uses provider defaults.</summary>
    HybridCacheEntryOptions? CacheOptions => null;
}

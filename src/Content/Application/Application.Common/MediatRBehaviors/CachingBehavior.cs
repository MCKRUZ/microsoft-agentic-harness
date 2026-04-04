using Application.Common.Interfaces.MediatR;
using MediatR;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Application.Common.MediatRBehaviors;

/// <summary>
/// Unified caching behavior using <see cref="HybridCache"/> (.NET 8+) which transparently
/// manages L1 (in-memory) and L2 (distributed/Redis) caching. Replaces the separate
/// <c>MemoryCachingBehavior</c> and <c>HybridMemoryCachingBehavior</c> from the reference implementation.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline position: 9 (innermost, immediately before handler). Handles two concerns:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="ICacheableQuery{TResponse}"/> — cache lookup before handler execution</description></item>
///   <item><description><see cref="ICacheInvalidation"/> — cache removal after successful command execution</description></item>
/// </list>
/// <para>
/// Cache invalidation and cache lookup are separate interfaces following CQRS:
/// queries are cached, commands invalidate. No <c>ClearCache</c> boolean flag.
/// </para>
/// </remarks>
public sealed class CachingBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly HybridCache _cache;
    private readonly ILogger<CachingBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    public CachingBehavior(HybridCache cache, ILogger<CachingBehavior<TRequest, TResponse>> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Cache invalidation for commands
        if (request is ICacheInvalidation invalidation)
        {
            var response = await next();

            var keys = invalidation.CacheKeysToInvalidate;
            var tasks = keys.Select(key => _cache.RemoveAsync(key, cancellationToken).AsTask());
            await Task.WhenAll(tasks);

            _logger.LogDebug("Invalidated {Count} cache keys for {RequestName}",
                keys.Count, typeof(TRequest).Name);

            return response;
        }

        // Cache lookup for queries
        if (request is ICacheableQuery<TResponse> cacheable)
        {
            var response = await _cache.GetOrCreateAsync(
                cacheable.CacheKey,
                async cancel => await next(),
                cacheable.CacheOptions,
                cancellationToken: cancellationToken);

            return response
                ?? throw new InvalidOperationException($"Cache returned null for key '{cacheable.CacheKey}'.");
        }

        return await next();
    }
}

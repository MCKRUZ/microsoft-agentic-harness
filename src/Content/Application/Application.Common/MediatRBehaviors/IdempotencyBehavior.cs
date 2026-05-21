using Application.Common.Interfaces.Idempotency;
using Application.Common.Interfaces.MediatR;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Common.MediatRBehaviors;

/// <summary>
/// Deduplicates retried requests by caching responses under an idempotency key.
/// Only applies to requests implementing <see cref="IIdempotentRequest"/>.
/// Non-idempotent requests pass through unchanged.
/// </summary>
/// <remarks>
/// Pipeline position: register early (after tracing, before validation) so duplicate
/// requests are rejected before validation and handler execution.
/// Requires <see cref="IIdempotencyStore"/> to be registered in the DI container.
/// </remarks>
/// <typeparam name="TRequest">The MediatR request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class IdempotencyBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IIdempotencyStore _store;
    private readonly ILogger<IdempotencyBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdempotencyBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="store">The idempotency store used to cache and retrieve responses.</param>
    /// <param name="logger">Logger for cache hit/miss diagnostics.</param>
    public IdempotencyBehavior(
        IIdempotencyStore store,
        ILogger<IdempotencyBehavior<TRequest, TResponse>> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IIdempotentRequest idempotentRequest)
            return await next();

        var key = idempotentRequest.IdempotencyKey;

        var cached = await _store.TryGetAsync(key, cancellationToken);
        if (cached is TResponse cachedResponse)
        {
            _logger.LogDebug(
                "Idempotent hit for {RequestName} with key '{Key}' — returning cached response",
                typeof(TRequest).Name, key);
            return cachedResponse;
        }

        var response = await next();

        await _store.SetAsync(key, response!, cancellationToken);

        _logger.LogDebug(
            "Idempotent miss for {RequestName} with key '{Key}' — response cached",
            typeof(TRequest).Name, key);

        return response;
    }
}

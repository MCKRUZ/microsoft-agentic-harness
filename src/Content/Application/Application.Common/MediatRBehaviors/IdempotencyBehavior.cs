using Application.Common.Interfaces.Idempotency;
using Application.Common.Interfaces.MediatR;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Common.MediatRBehaviors;

/// <summary>
/// Deduplicates retried requests by caching responses under an idempotency key.
/// Only applies to requests implementing <see cref="IIdempotentRequest"/>.
/// Non-idempotent requests pass through unchanged.
/// </summary>
/// <remarks>
/// Pipeline position: registered as the outermost behavior in
/// <c>AddApplicationCommonDependencies</c> so a duplicate request short-circuits to the
/// cached response before validation and handler execution run. Successful responses are
/// cached; failure <see cref="Result"/>s are deliberately not cached so legitimate retries
/// after a transient failure still re-execute the handler.
/// Requires <see cref="IIdempotencyStore"/> to be registered in the DI container — the default
/// <c>InMemoryIdempotencyStore</c> is registered alongside this behavior.
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

        // Never cache expected failures: this codebase returns Result<T> for transient/expected
        // errors rather than throwing. Caching a failure would replay it to every legitimate retry,
        // defeating the purpose of idempotent re-execution. Only successful responses are cached.
        if (response is Result { IsSuccess: false })
        {
            _logger.LogDebug(
                "Idempotent miss for {RequestName} with key '{Key}' — failure result not cached",
                typeof(TRequest).Name, key);

            return response;
        }

        await _store.SetAsync(key, response!, cancellationToken);

        _logger.LogDebug(
            "Idempotent miss for {RequestName} with key '{Key}' — response cached",
            typeof(TRequest).Name, key);

        return response;
    }
}

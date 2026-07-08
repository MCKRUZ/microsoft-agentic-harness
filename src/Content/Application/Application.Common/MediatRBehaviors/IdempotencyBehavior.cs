using Application.Common.Interfaces.Idempotency;
using Application.Common.Interfaces.MediatR;
using Application.Common.Interfaces.Security;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Common.MediatRBehaviors;

/// <summary>
/// Deduplicates retried requests by caching responses under a per-user idempotency key.
/// Only applies to requests implementing <see cref="IIdempotentRequest"/>.
/// Non-idempotent requests pass through unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Pipeline position:</strong> registered <em>after</em> <c>AuthorizationBehavior</c> in
/// <c>AddApplicationCommonDependencies</c> so a replayed idempotency key still passes validation
/// and authorization before any cached response can be served. Registering it as the outermost
/// behavior (as it originally was) let a duplicate short-circuit to the cached response before the
/// auth check ran — an access-control bypass. It must never move back ahead of authorization.
/// </para>
/// <para>
/// <strong>Per-user scoping:</strong> the caller-supplied <see cref="IIdempotentRequest.IdempotencyKey"/>
/// is composed with the current <see cref="IUser.Id"/> and the request type into the store key
/// (<c>{userId}:{requestType}:{key}</c>). Without this, one user replaying another user's key would
/// receive the other user's cached response (cross-user disclosure).
/// </para>
/// <para>
/// Successful responses are cached; failure <see cref="Result"/>s are deliberately not cached so a
/// legitimate retry after a transient failure re-executes the handler. Concurrent duplicates are
/// coalesced onto a single execution by <see cref="IIdempotencyStore.GetOrExecuteAsync"/>.
/// </para>
/// </remarks>
/// <typeparam name="TRequest">The MediatR request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class IdempotencyBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    /// <summary>Key-segment stand-in for an unauthenticated caller with no identity.</summary>
    private const string AnonymousUserId = "anonymous";

    private readonly IIdempotencyStore _store;
    private readonly IUser _user;
    private readonly ILogger<IdempotencyBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdempotencyBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="store">The idempotency store used to cache and retrieve responses.</param>
    /// <param name="user">The current user context, used to scope idempotency keys per identity.</param>
    /// <param name="logger">Logger for cache hit/miss diagnostics.</param>
    public IdempotencyBehavior(
        IIdempotencyStore store,
        IUser user,
        ILogger<IdempotencyBehavior<TRequest, TResponse>> logger)
    {
        _store = store;
        _user = user;
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

        var scopedKey = BuildScopedKey(idempotentRequest.IdempotencyKey);
        var executed = false;

        // Never cache expected failures: this codebase returns Result<T> for transient/expected
        // errors rather than throwing. Caching a failure would replay it to every legitimate retry.
        var response = await _store.GetOrExecuteAsync(
            scopedKey,
            async () =>
            {
                executed = true;
                return (object)(await next())!;
            },
            static value => value is not Result { IsSuccess: false },
            cancellationToken);

        _logger.LogDebug(
            "Idempotent {Outcome} for {RequestName} (user '{UserId}')",
            executed ? "miss — handler executed" : "hit — cached response returned",
            typeof(TRequest).Name,
            _user.Id ?? AnonymousUserId);

        return (TResponse)response;
    }

    private string BuildScopedKey(string idempotencyKey)
    {
        var userId = string.IsNullOrEmpty(_user.Id) ? AnonymousUserId : _user.Id;
        return $"{userId}:{typeof(TRequest).FullName}:{idempotencyKey}";
    }
}

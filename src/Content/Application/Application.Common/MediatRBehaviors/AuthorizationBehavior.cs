using System.Collections.Concurrent;
using System.Reflection;
using Application.Common.Attributes.SecurityAttributes;
using Application.Common.Exceptions.ExceptionTypes;
using Application.Common.Interfaces.Security;
using Domain.Common.Helpers;
using Domain.Common;
using MediatR;

namespace Application.Common.MediatRBehaviors;

/// <summary>
/// Enforces role-based and policy-based authorization on requests decorated with
/// <see cref="AuthorizeAttribute"/>. Caches attribute reflection results per request type.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline position: 5 (after audit, before tool permissions).
/// </para>
/// <para>
/// When <c>TResponse</c> is <see cref="Result"/> or <see cref="Result{T}"/>,
/// returns a failure result instead of throwing. For non-Result responses,
/// throws <see cref="ForbiddenAccessException"/> as a fallback.
/// </para>
/// </remarks>
public sealed class AuthorizationBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private static readonly ConcurrentDictionary<Type, (AuthorizeAttribute[] RoleAttrs, AuthorizeAttribute[] PolicyAttrs)> AttributeCache = new();

    private readonly IUser _user;
    private readonly IIdentityService _identityService;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthorizationBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    public AuthorizationBehavior(IUser user, IIdentityService identityService)
    {
        _user = user;
        _identityService = identityService;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var (roleAttrs, policyAttrs) = AttributeCache.GetOrAdd(
            typeof(TRequest),
            static t =>
            {
                var all = t.GetCustomAttributes<AuthorizeAttribute>().ToArray();
                return (
                    all.Where(a => !string.IsNullOrWhiteSpace(a.Roles)).ToArray(),
                    all.Where(a => !string.IsNullOrWhiteSpace(a.Policy)).ToArray());
            });

        if (roleAttrs.Length == 0 && policyAttrs.Length == 0)
            return await next();

        if (string.IsNullOrEmpty(_user.Id))
        {
            if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.Unauthorized), "Authentication is required.", out var unauthorizedResult))
                return unauthorizedResult;
            throw new UnauthorizedAccessException("Authentication is required.");
        }

        // Role checks — OR within attribute, AND across attributes
        foreach (var attribute in roleAttrs)
        {
            var roles = attribute.Roles.Split(',', StringSplitOptions.TrimEntries);
            bool authorized;
            if (roles.Length == 1)
            {
                authorized = await _identityService.IsInRoleAsync(_user.Id, roles[0]);
            }
            else
            {
                var results = await Task.WhenAll(
                    roles.Select(role => _identityService.IsInRoleAsync(_user.Id, role)));
                authorized = Array.IndexOf(results, true) >= 0;
            }

            if (!authorized)
            {
                var reason = $"User does not have any of the required roles: {attribute.Roles}";
                if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.Forbidden), reason, out var forbiddenResult))
                    return forbiddenResult;
                throw new ForbiddenAccessException(reason);
            }
        }

        // Policy checks — all must pass
        foreach (var attribute in policyAttrs)
        {
            if (!await _identityService.AuthorizeAsync(_user.Id, attribute.Policy))
            {
                var reason = $"User does not satisfy the required policy: {attribute.Policy}";
                if (ResultHelper.TryCreateFailure<TResponse>(nameof(Result.Forbidden), reason, out var policyResult))
                    return policyResult;
                throw new ForbiddenAccessException(reason);
            }
        }

        return await next();
    }
}

using System.Security.Claims;
using Presentation.Common.Extensions;

namespace Presentation.ExecutionApi.Services;

/// <summary>
/// Resolves a stable per-caller identifier from an authenticated principal, used to bind bundle handles/runs
/// to their owner and to partition the rate limiter.
/// </summary>
/// <remarks>
/// <para>
/// This is a named seam over <see cref="ClaimsPrincipalExtensions.GetUserIdOrNull"/>, not a second
/// precedence ladder. It deliberately does NOT resolve claims itself: bundle ownership and knowledge scope
/// (which decides plan visibility) must accept exactly the same token shapes. While they disagreed, a token
/// carrying <c>sub</c> but no <c>oid</c> earned bundle ownership while its knowledge scope stayed null —
/// and a null owner reads as GLOBAL, not private, so that caller's plans were readable by everyone.
/// </para>
/// <para>
/// Only identifiers guaranteed unique per principal are accepted (<c>oid</c>, then <c>sub</c>, each in raw
/// or JWT-mapped form); the display name (<c>name</c>) is excluded because it is not unique.
/// </para>
/// </remarks>
public static class BundleCallerIdentity
{
    /// <summary>
    /// Returns a stable, per-principal-unique identifier, or null when the principal carries none. A null
    /// result means the caller has no durable identity to own resources under: callers treat that as a
    /// rejection rather than bucketing the caller into a shared identity.
    /// </summary>
    /// <param name="principal">The authenticated principal.</param>
    /// <returns>The caller's stable identifier, or <c>null</c>.</returns>
    public static string? StableId(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.GetUserIdOrNull();
    }
}

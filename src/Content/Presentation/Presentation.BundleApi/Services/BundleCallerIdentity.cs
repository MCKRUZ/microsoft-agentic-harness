using System.Security.Claims;

namespace Presentation.BundleApi.Services;

/// <summary>
/// Resolves a stable per-caller identifier from an authenticated principal, used to bind bundle handles/runs
/// to their owner and to partition the rate limiter. Prefers identifiers that are guaranteed unique per
/// principal within the tenant — the Entra object id (<c>oid</c>) then the subject (<c>sub</c>/name-identifier)
/// — and deliberately excludes the display name (<c>name</c>), which is not unique.
/// </summary>
public static class BundleCallerIdentity
{
    /// <summary>
    /// Returns a stable, per-principal-unique identifier, or null when the principal carries none. A null
    /// result means the caller has no durable identity to own resources under: callers treat that as a
    /// rejection rather than bucketing the caller into a shared identity.
    /// </summary>
    /// <param name="principal">The authenticated principal.</param>
    public static string? StableId(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return NullIfBlank(principal.FindFirstValue("oid"))
            ?? NullIfBlank(principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier"))
            ?? NullIfBlank(principal.FindFirstValue(ClaimTypes.NameIdentifier))
            ?? NullIfBlank(principal.FindFirstValue("sub"));
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

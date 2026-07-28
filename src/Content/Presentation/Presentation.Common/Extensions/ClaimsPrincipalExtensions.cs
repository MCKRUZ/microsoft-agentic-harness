using System.Security.Claims;
using Application.Core.Validation;

namespace Presentation.Common.Extensions;

/// <summary>
/// The single authority on how the harness derives a caller's stable identity and tenant from an
/// authenticated <see cref="ClaimsPrincipal"/>.
/// </summary>
/// <remarks>
/// <para>
/// Everything that binds a record to its owner resolves identity through here — knowledge scope
/// (<c>KnowledgeScopeInitializer</c>, which decides plan/graph/memory ownership), bundle ownership and
/// rate-limit partitioning (<c>BundleCallerIdentity</c>), and the AgentHub controllers/hub. A second
/// precedence ladder anywhere else is a defect waiting to happen: when one resolver accepts a token
/// shape the other rejects, the caller gets ownership under one subsystem and a <em>null</em> identity
/// under the other — and a null owner is treated as GLOBAL (world-readable) by
/// <c>PlannerScopeFilter.VisibleTo</c>, not as private.
/// </para>
/// <para>
/// Resolution searches the union of each claim type's equivalent forms via
/// <see cref="ApproverClaimTypes.EquivalentFormsOf"/> rather than a hand-written list. JWT inbound claim
/// mapping REMAPS short names on validated tokens — <c>oid</c> arrives as the objectidentifier URI,
/// <c>sub</c> as <see cref="ClaimTypes.NameIdentifier"/> — while dev/test handlers mint the raw short
/// names. A raw <c>FindFirst("sub")</c> therefore finds nothing on a real production token.
/// </para>
/// </remarks>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Claim types that may establish a caller's stable identity, in precedence order. Both are
    /// issuer-asserted and immutable: <c>oid</c> is the Entra object id, <c>sub</c> the OIDC subject.
    /// <c>sub</c> is not a concession to exotic providers — <c>oid</c> is the Entra-ism, and plenty of
    /// OIDC issuers emit only <c>sub</c>. Mutable sign-in names (<c>upn</c>, <c>preferred_username</c>)
    /// and display names are deliberately excluded: they are reassignable, so they must never key
    /// ownership.
    /// </summary>
    private static readonly string[] StableIdentityClaimTypes = ["oid", "sub"];

    /// <summary>
    /// Returns the caller's stable identity. Throws <see cref="InvalidOperationException"/> when none is
    /// present — this should never occur for endpoints protected by <c>[Authorize]</c> with a valid token.
    /// </summary>
    /// <param name="principal">The authenticated principal.</param>
    /// <returns>The caller's stable identity.</returns>
    /// <exception cref="InvalidOperationException">The principal carries no usable stable identity.</exception>
    public static string GetUserId(this ClaimsPrincipal principal) =>
        principal.GetUserIdOrNull()
        ?? throw new InvalidOperationException(
            "The authenticated principal carries no usable stable identity claim ('oid' or 'sub', in " +
            "either their raw or JWT-mapped form).");

    /// <summary>
    /// Returns the Azure AD tenant ID (<c>tid</c>) of the authenticated user, or <c>null</c> when
    /// the token carries no tenant claim. Unlike <see cref="GetUserId"/> this does not throw —
    /// tenant is optional (single-tenant deployments and the dev auth bypass have no <c>tid</c>),
    /// and the knowledge scope falls back to its configured default tenant when this is null.
    /// </summary>
    /// <param name="principal">The authenticated principal.</param>
    /// <returns>The tenant ID, or <c>null</c>.</returns>
    public static string? GetTenantId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // Azure AD tokens carry the tenant ID in the standard "tid" claim or the namespaced
        // "http://schemas.microsoft.com/identity/claims/tenantid" claim.
        var tid = principal.FindFirstValue("tid")
            ?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid");

        return string.IsNullOrWhiteSpace(tid) ? null : tid;
    }

    /// <summary>
    /// Returns the authenticated caller's stable identity, or <c>null</c> when the principal is
    /// unauthenticated, carries no stable identity claim, or carries an ambiguous one. Non-throwing
    /// companion to <see cref="GetUserId"/> for entry-point code that runs before authorization is
    /// guaranteed.
    /// </summary>
    /// <param name="principal">The principal to resolve.</param>
    /// <returns>The caller's stable identity, or <c>null</c>.</returns>
    /// <remarks>
    /// Within a rung, more than one distinct value is an ambiguous identity and yields <c>null</c>
    /// rather than a silent first-pick — an attacker who can smuggle a second instance of the claim must
    /// not get to choose which one wins (the rule <c>DriftCallerIdentity</c> already applies). An
    /// ambiguous rung stops resolution outright instead of falling through to the next one, so poisoning
    /// <c>oid</c> cannot be used to force selection of an attacker-controlled <c>sub</c>.
    /// </remarks>
    public static string? GetUserIdOrNull(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity?.IsAuthenticated != true)
            return null;

        foreach (var claimType in StableIdentityClaimTypes)
        {
            var values = ApproverClaimTypes.EquivalentFormsOf(claimType)
                .SelectMany(principal.FindAll)
                .Select(claim => claim.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToList();

            if (values.Count > 1)
                return null;

            if (values.Count == 1)
                return values[0];
        }

        return null;
    }
}

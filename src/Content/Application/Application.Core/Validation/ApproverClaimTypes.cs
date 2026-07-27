using System.Security.Claims;

namespace Application.Core.Validation;

/// <summary>
/// The single authority on which claim types may drive escalation approver identity, and on the
/// equivalent wire/mapped forms each one appears under at runtime. The config validator's
/// allowlist and the controller's claim resolution both read from here, so they cannot drift.
/// </summary>
/// <remarks>
/// <para>
/// The equivalence map exists because of JWT inbound claim mapping:
/// <c>System.IdentityModel.Tokens.Jwt</c> (the handler behind
/// <c>AddMicrosoftIdentityWebApiAuthentication</c>) REMAPS short claim names on validated Entra
/// tokens — <c>oid</c> becomes <c>http://schemas.microsoft.com/identity/claims/objectidentifier</c>,
/// <c>sub</c> becomes <see cref="ClaimTypes.NameIdentifier"/>, <c>upn</c> becomes
/// <see cref="ClaimTypes.Upn"/> — while dev/test handlers mint the raw short names.
/// Resolving only the configured short name would find ZERO claims on real production tokens
/// (the repo's <c>ClaimsPrincipalExtensions.GetUserIdOrNull()</c> works around the same
/// remapping). Callers must therefore resolve across ALL equivalent forms.
/// <c>preferred_username</c> has no inbound mapping and passes through unchanged.
/// </para>
/// <para>
/// Unioning forms is preferred over <c>MapInboundClaims = false</c> because disabling the map is
/// host-global and would silently change claim shapes for every other claims consumer in the
/// host (user-id resolution, roles plumbing, knowledge scoping).
/// </para>
/// </remarks>
public static class ApproverClaimTypes
{
    private static readonly Dictionary<string, IReadOnlyList<string>> EquivalentForms =
        new(StringComparer.Ordinal)
        {
            ["oid"] = ["oid", "http://schemas.microsoft.com/identity/claims/objectidentifier"],
            ["sub"] = ["sub", ClaimTypes.NameIdentifier],
            ["preferred_username"] = ["preferred_username"],
            ["upn"] = ["upn", ClaimTypes.Upn],
        };

    /// <summary>
    /// The only claim types allowed to drive escalation approver identity (configuration-facing
    /// short names). All four are issuer-asserted: <c>oid</c>/<c>sub</c> are immutable
    /// object/subject ids; <c>upn</c> and <c>preferred_username</c> are sign-in names (mutable —
    /// see <see cref="Mutable"/>). Anything user-editable (display name, unverified email) is
    /// rejected at startup so it can never select the approver.
    /// </summary>
    public static IReadOnlyList<string> Allowed { get; } =
        ["oid", "sub", "preferred_username", "upn"];

    /// <summary>
    /// The allowed claim types that are nonetheless mutable and reassignable (a departed
    /// approver's UPN can be reissued to a new hire, who then inherits roster entries naming
    /// it). Hosts configured with one of these get a startup warning; <c>oid</c> is the
    /// production recommendation.
    /// </summary>
    public static IReadOnlyList<string> Mutable { get; } =
        ["preferred_username", "upn"];

    /// <summary>
    /// Every claim-type form the configured type can appear under on a runtime principal: the
    /// short name itself plus its known JWT inbound-mapped URI. Resolution must search the whole
    /// union — production tokens carry the mapped form, dev/test principals the short form.
    /// Unknown (non-allowlisted) types fall back to the raw configured name only, which keeps
    /// resolution fail-closed and well-defined even when the allowlist is not enforced (the
    /// validator skips it while escalation is disabled).
    /// </summary>
    /// <param name="claimType">The configured approver claim type.</param>
    public static IReadOnlyList<string> EquivalentFormsOf(string claimType)
    {
        ArgumentNullException.ThrowIfNull(claimType);
        return EquivalentForms.TryGetValue(claimType, out var forms) ? forms : [claimType];
    }
}

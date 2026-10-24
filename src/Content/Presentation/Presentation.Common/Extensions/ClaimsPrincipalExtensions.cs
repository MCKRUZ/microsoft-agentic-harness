using System.Security.Claims;
using Application.Core.Validation;
using Domain.AI.Escalation;

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
                // Ordinal, matching how ownership is compared downstream (record.UserId != callerId).
                // Deduping case-insensitively here while comparing ordinally there would let two
                // claims differing only in case collapse to one, and whichever arrived first would
                // silently decide the stored owner. Treating them as ambiguous fails closed instead.
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToList();

            if (values.Count > 1)
                return null;

            if (values.Count == 1)
                return values[0];
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="principal"/> is an application acting as itself (a client-credentials, app-only token)
    /// rather than a person. Every caller using that application shares its identity, so an action scoped to "this
    /// caller" — erasing one's own data (#907) — would act on all of them.
    /// </summary>
    /// <param name="principal">The principal to check.</param>
    /// <returns><see langword="true"/> for an app-only token; <see langword="false"/> otherwise, including unauthenticated.</returns>
    /// <remarks>
    /// <para>
    /// <b>Any issuer.</b> The optional <c>idtyp</c> claim is <c>app</c> only in an app-only token, which Entra calls
    /// "the most accurate way for an API to determine if a token is an app token or an app+user token"; a tenant must
    /// turn it on. A subject equal to the calling client's id (<c>azp</c>, <c>appid</c>, <c>client_id</c> or Okta's
    /// <c>cid</c>) is the client acting as itself, the shape RFC 9068 gives a client-credentials token; a person's subject
    /// is never a client id. Auth0 marks its machine tokens with <c>gty=client-credentials</c> instead, because their
    /// subject is the client id with <c>@clients</c> appended. Other providers' service accounts (Keycloak's, for one)
    /// carry neither shape and are not recognised; a host using one should turn on an explicit marker.
    /// </para>
    /// <para>
    /// <b>Entra workforce and External ID issuers only</b> (<see cref="IsEntraIssued"/>). Entra emits <c>scp</c> "only
    /// for user tokens", and Microsoft.Identity.Web refuses a bearer token carrying neither <c>scp</c> nor <c>roles</c>,
    /// so an accepted token without <c>scp</c> is an application's. Microsoft's documented check also applies there:
    /// an app-only token's <c>sub</c> is the service principal, equal to its <c>oid</c>, while a person's <c>sub</c> is
    /// a pairwise id. Neither holds elsewhere: other providers often issue a person no <c>scp</c>, and Azure AD B2C
    /// fills a person's <c>sub</c> with their object id by default, so <c>oid</c> equals <c>sub</c> for every B2C
    /// user. Refusing a real person their own erasure is the wrong way to fail, so those checks stay behind the gate.
    /// </para>
    /// <para>
    /// Every form of each claim is compared, as <see cref="GetUserIdOrNull"/> searches them, and any value equal to any
    /// other counts: a token carrying two of either is already ambiguous, so this fails closed.
    /// </para>
    /// <para>
    /// It cannot see a shared account signed in as a person (a "robot" user), which looks like anyone else's token. An
    /// Entra tenant whose tokens are issued from a custom domain is not recognised as Entra, so only <c>idtyp</c> and the
    /// client-id checks apply there; turning on <c>idtyp</c> closes that. In the other direction, an Entra token that
    /// carries app roles but no scopes is refused even if a person holds it; that fails safe, since an operator can still
    /// erase them.
    /// </para>
    /// </remarks>
    public static bool IsApplicationOnly(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity?.IsAuthenticated != true)
            return false;

        if (principal.FindAll("idtyp").Any(c => string.Equals(c.Value, "app", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (principal.FindAll("gty").Any(c => string.Equals(c.Value, "client-credentials", StringComparison.Ordinal)))
            return true;

        var subjects = ValuesOf(principal, "sub");
        if (subjects.Overlaps(ClientIdClaimTypes.SelectMany(principal.FindAll).Select(c => c.Value)))
            return true;

        if (!IsEntraIssued(principal))
            return false;

        return !ScopeClaimTypes.Any(type => principal.HasClaim(c => c.Type == type))
            || subjects.Overlaps(ValuesOf(principal, "oid"));
    }

    /// <summary>
    /// The claims naming the client that requested the token: Entra v2 <c>azp</c>, v1 <c>appid</c>, RFC 9068
    /// <c>client_id</c>, Okta <c>cid</c>.
    /// </summary>
    private static readonly string[] ClientIdClaimTypes = ["azp", "appid", "client_id", "cid"];

    /// <summary><c>scp</c> in its raw and JWT inbound-mapped forms.</summary>
    private static readonly string[] ScopeClaimTypes = ["scp", "http://schemas.microsoft.com/identity/claims/scope"];

    /// <summary>
    /// The hosts that issue Microsoft Entra workforce and External ID tokens, in every cloud. Azure AD B2C
    /// (<c>b2clogin.com</c> or a custom domain) is deliberately absent; see <see cref="IsApplicationOnly"/>.
    /// </summary>
    private static readonly string[] EntraIssuerHosts =
    [
        "login.microsoftonline.com", "sts.windows.net", "login.microsoftonline.us",
        "login.partner.microsoftonline.cn", "login.chinacloudapi.cn", "sts.chinacloudapi.cn",
    ];

    /// <summary>
    /// Whether the token came from Entra workforce or External ID (<c>*.ciamlogin.com</c>), read from the <c>iss</c>
    /// claim or, when the handler did not keep it, from the issuer it stamped on the identity claims.
    /// </summary>
    private static bool IsEntraIssued(ClaimsPrincipal principal)
    {
        var issuers = principal.FindAll("iss").Select(c => c.Value)
            .Concat(ApproverClaimTypes.EquivalentFormsOf("oid").Concat(ApproverClaimTypes.EquivalentFormsOf("sub"))
                .SelectMany(principal.FindAll)
                .Select(c => c.Issuer));

        return issuers.Any(issuer =>
            Uri.TryCreate(issuer, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (EntraIssuerHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".ciamlogin.com", StringComparison.OrdinalIgnoreCase)));
    }

    private static HashSet<string> ValuesOf(ClaimsPrincipal principal, string claimType) =>
        ApproverClaimTypes.EquivalentFormsOf(claimType)
            .SelectMany(principal.FindAll)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Returns the caller's approver identity — the name compared against escalation rosters — or
    /// <c>null</c> when the principal does not carry exactly one usable value for
    /// <paramref name="approverClaimType"/>.
    /// </summary>
    /// <param name="principal">The principal to resolve.</param>
    /// <param name="approverClaimType">
    /// The configured claim type, from <c>EscalationConfig.ApproverClaimType</c>. A server-side
    /// setting, never a request parameter: the approver name must come from the validated token, so a
    /// caller can never assert an identity the token does not carry.
    /// </param>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="GetUserIdOrNull"/> and deliberately so. That answers "who owns this
    /// record", and is restricted to immutable claims because ownership must not follow a reassignable
    /// name. This answers "which roster entries is this person", which is a different question with a
    /// different, operator-chosen claim — rosters are authored with human-readable names by default.
    /// The two can resolve to different strings for the same person, which is exactly why anything
    /// comparing a caller to a roster must use this one.
    /// </para>
    /// <para>
    /// Lives here, with the other identity resolution, because three surfaces need it — the escalation
    /// decision path, the change-proposal review path, and workflow submission's self-approval check —
    /// and a per-controller copy is how one of them silently drifts back to a case-sensitive or
    /// unmapped comparison.
    /// </para>
    /// <para>
    /// Resolution searches the union of the claim type's equivalent forms, because production tokens
    /// carry the JWT inbound-mapped form while dev/test principals carry the short one. Across that
    /// union, more than one distinct value (per <c>ApproverNames.Comparer</c>) is an ambiguous identity
    /// and yields <c>null</c> rather than a silent first-pick — someone who can smuggle a second
    /// instance of the claim must not get to choose which one wins. The same value appearing under both
    /// forms counts as one.
    /// </para>
    /// </remarks>
    public static string? GetApproverNameOrNull(this ClaimsPrincipal principal, string approverClaimType)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(approverClaimType);

        // Matching GetUserIdOrNull rather than reading claims off whatever is handed in. Every caller
        // today sits behind [Authorize], so this is unreachable — but this method is now the single
        // authority for roster identity, and an authority whose two halves disagree about what an
        // unauthenticated principal means is what the next caller trips over.
        if (principal.Identity?.IsAuthenticated != true)
            return null;

        var values = ApproverClaimTypes.EquivalentFormsOf(approverClaimType)
            .SelectMany(principal.FindAll)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(ApproverNames.Comparer)
            .Take(2)
            .ToList();

        return values.Count == 1 ? values[0] : null;
    }
}

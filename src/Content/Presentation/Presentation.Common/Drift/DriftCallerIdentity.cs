using System.Security.Claims;
using Application.Core.Validation;

namespace Presentation.Common.Drift;

/// <summary>
/// The single authority on how the drift HTTP surface derives a caller identity from an
/// authenticated principal. Both the controller (which stamps the identity into the audit
/// trail) and the host's rate-limit partitioner read from here, so a host that configures a
/// non-default <c>CallerIdentityClaimType</c> cannot end up throttling on one claim while
/// auditing on another.
/// </summary>
public static class DriftCallerIdentity
{
    /// <summary>
    /// Resolves the caller identity from the configured claim type, or null when the principal
    /// does not carry exactly one usable value.
    /// </summary>
    /// <remarks>
    /// Resolution searches the union of the configured type's equivalent forms
    /// (<see cref="ApproverClaimTypes.EquivalentFormsOf"/>): production tokens carry the JWT
    /// inbound-MAPPED form (e.g. <c>oid</c> arrives as the objectidentifier URI), dev/test
    /// principals the short form — searching only one of them would reject every legitimate
    /// operator on the other auth path. Across that union, more than one distinct value is an
    /// ambiguous identity and yields null rather than a silent first-pick: an attacker who can
    /// smuggle a second instance of the claim must not get to choose which one wins. The same
    /// value appearing under both forms counts as one.
    /// </remarks>
    /// <param name="principal">The authenticated principal.</param>
    /// <param name="claimType">The configured caller identity claim type.</param>
    /// <returns>The single resolved identity, or <see langword="null"/> when absent or ambiguous.</returns>
    public static string? Resolve(ClaimsPrincipal? principal, string claimType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);

        if (principal is null)
            return null;

        var values = ApproverClaimTypes.EquivalentFormsOf(claimType)
            .SelectMany(principal.FindAll)
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();

        return values.Count == 1 ? values[0] : null;
    }
}

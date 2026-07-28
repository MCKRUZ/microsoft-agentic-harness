namespace Domain.AI.Permissions;

/// <summary>
/// How much authority a rule flagged <see cref="ToolPermissionRule.IsAuthoritativeBaseline"/> carries
/// relative to the baselines emitted by <em>other</em> rule providers. Baselines within the same tier
/// arbitrate against each other by pattern specificity and restrictiveness as usual; the tier only
/// decides what happens when providers disagree about a name.
/// </summary>
/// <remarks>
/// <para>
/// The tier exists because "authoritative baseline" is not one thing. Most baselines express a
/// <em>default posture</em> for a set of tools — a plugin saying "my own tools may auto-run". A few
/// express a <em>grant boundary</em>: the outer edge of what a caller was authorised to do at all.
/// Those two are not peers, and leaving them as peers is a real hole: a per-name default from one
/// provider is more specific than a boundary's catch-all, so specificity arbitration alone lets the
/// default widen the boundary.
/// </para>
/// <para>
/// Declaring the rank on the rule keeps the resolver out of the business of recognising providers. A
/// provider that is expressing a boundary says so; the resolver arbitrates on what was declared rather
/// than on who declared it.
/// </para>
/// </remarks>
public enum PermissionBaselineTier
{
    /// <summary>
    /// An ordinary baseline: a default posture for the tools it names, which other baselines may
    /// outrank by being more specific or more restrictive. This is what every rule carries unless a
    /// provider deliberately declares otherwise.
    /// </summary>
    Default = 0,

    /// <summary>
    /// A baseline that describes the outer edge of an authorisation grant rather than a default within
    /// it. No <see cref="Default"/>-tier baseline may resolve to a <em>less</em> restrictive behavior
    /// than the boundary allows for that tool, whatever its pattern specificity — a boundary can only be
    /// tightened from outside, never widened. Emitted by the capability envelope, whose rules are the
    /// host's per-caller grant for a bundle run.
    /// </summary>
    GrantBoundary = 1
}

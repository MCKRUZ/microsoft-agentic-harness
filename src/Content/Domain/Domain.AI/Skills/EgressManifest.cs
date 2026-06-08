using Domain.AI.Egress;

namespace Domain.AI.Skills;

/// <summary>
/// The egress section of a SKILL.md manifest. Carries the per-skill outbound
/// allowlist that augments the global <c>EgressConfig.DefaultAllowlist</c> at
/// policy-resolve time. A skill that omits the section (or declares an empty
/// allowlist) inherits the global default with no additions — the layer remains
/// default-deny.
/// </summary>
/// <remarks>
/// <para>
/// PR-3c introduces this manifest section so individual skills can declare the
/// specific hosts they need without widening the harness-wide default. The
/// per-skill allowlist is ADDITIVE — it never narrows the default and never
/// overrides another skill's allowlist. The resolver computes the effective
/// policy by concatenating the default and per-skill entries (de-duplication is
/// performed by the policy's match algorithm; duplicates are harmless).
/// </para>
/// <para>
/// Entries reuse <see cref="EgressAllowlistEntry"/> verbatim so the same
/// validation rules (leftmost-label wildcards only, http/https schemes only,
/// explicit ports) apply at both the configuration and skill-manifest entry
/// points. A regex on the host portion is an SSRF vector — this manifest
/// section keeps the surface narrow on purpose.
/// </para>
/// </remarks>
public sealed record EgressManifest
{
    /// <summary>
    /// The per-skill allowlist entries declared in <c>egress.allowlist</c> in
    /// SKILL.md frontmatter. May be empty; an empty list means the skill adds
    /// nothing to the default and inherits the harness-wide allowlist as-is.
    /// </summary>
    public IReadOnlyList<EgressAllowlistEntry> Allowlist { get; init; } = [];

    /// <summary>True when the skill declares at least one allowlist entry.</summary>
    public bool HasAllowlist => Allowlist.Count > 0;
}

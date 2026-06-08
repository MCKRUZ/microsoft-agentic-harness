namespace Domain.AI.Skills;

/// <summary>
/// YAML-shape mirror of <see cref="EgressManifest"/> produced by the SKILL.md
/// frontmatter parser. Holds the raw deserialized shape — mutable lists,
/// nullable fields, no invariants enforced — so the parser can populate it
/// incrementally before <see cref="EgressManifestSectionMapper.Map"/> projects
/// it onto the immutable domain record.
/// </summary>
/// <remarks>
/// <para>
/// The split mirrors the same boundary established by the egress configuration
/// layer (<c>EgressAllowlistConfigEntry</c> → <c>EgressAllowlistEntry</c>): the
/// parse-shape type accepts what the YAML loader hands it, and the mapper
/// produces the immutable domain shape that the validator and policy operate
/// on. The two-step keeps the domain free of mutable, nullable parse-state.
/// </para>
/// </remarks>
public sealed class EgressManifestSection
{
    /// <summary>Raw allowlist entries deserialized from the YAML <c>allowlist:</c> sequence.</summary>
    public List<EgressAllowlistEntrySection> Allowlist { get; set; } = [];
}

/// <summary>
/// YAML-shape mirror of <see cref="Domain.AI.Egress.EgressAllowlistEntry"/>.
/// Mutable, nullable, no invariants — populated by the parser, projected onto
/// the immutable domain record at the boundary.
/// </summary>
public sealed class EgressAllowlistEntrySection
{
    /// <summary>The exact host declared by the <c>host:</c> YAML field. Null when <see cref="HostPattern"/> is set.</summary>
    public string? Host { get; set; }

    /// <summary>The leftmost-label wildcard declared by the <c>hostPattern:</c> YAML field. Null when <see cref="Host"/> is set.</summary>
    public string? HostPattern { get; set; }

    /// <summary>The schemes declared by the <c>schemes:</c> YAML sequence.</summary>
    public List<string> Schemes { get; set; } = [];

    /// <summary>The ports declared by the <c>ports:</c> YAML sequence.</summary>
    public List<int> Ports { get; set; } = [];
}

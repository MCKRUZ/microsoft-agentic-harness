using Domain.AI.Egress;
using Domain.AI.Skills;

namespace Infrastructure.AI.Skills;

/// <summary>
/// Maps the parse-shape <see cref="EgressManifestSection"/> produced by the
/// SKILL.md frontmatter reader onto the immutable <see cref="EgressManifest"/>
/// domain record. Lives at the parser/domain boundary so the domain type does
/// not depend on the parse intermediary and the parse type does not depend on
/// immutability concerns.
/// </summary>
internal static class EgressManifestSectionMapper
{
    /// <summary>Map a parsed section to an immutable manifest.</summary>
    /// <param name="section">The parsed section. May be null.</param>
    /// <returns>The immutable manifest, or null when <paramref name="section"/> is null.</returns>
    public static EgressManifest? Map(EgressManifestSection? section)
    {
        if (section is null)
        {
            return null;
        }

        return new EgressManifest
        {
            Allowlist = section.Allowlist
                .Select(MapEntry)
                .ToArray()
        };
    }

    private static EgressAllowlistEntry MapEntry(EgressAllowlistEntrySection entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new EgressAllowlistEntry
        {
            Host = string.IsNullOrWhiteSpace(entry.Host) ? null : entry.Host.Trim(),
            HostPattern = string.IsNullOrWhiteSpace(entry.HostPattern) ? null : entry.HostPattern.Trim(),
            Schemes = entry.Schemes.Select(s => s.Trim()).ToArray(),
            Ports = entry.Ports.ToArray()
        };
    }
}

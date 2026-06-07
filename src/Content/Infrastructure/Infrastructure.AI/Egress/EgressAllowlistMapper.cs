using Domain.AI.Egress;
using Domain.Common.Config.AI;

namespace Infrastructure.AI.Egress;

/// <summary>
/// Maps the configuration-shaped <see cref="EgressAllowlistConfigEntry"/> onto
/// the domain-shaped <see cref="EgressAllowlistEntry"/>. Lives at the
/// configuration/domain boundary so the domain type does not depend on
/// configuration concerns and the configuration type does not depend on
/// <c>Domain.AI</c>.
/// </summary>
internal static class EgressAllowlistMapper
{
    /// <summary>Map a list of config entries to a list of domain entries.</summary>
    /// <param name="entries">The configuration entries, possibly empty.</param>
    /// <returns>A new list of domain entries. Never null.</returns>
    public static IReadOnlyList<EgressAllowlistEntry> Map(IEnumerable<EgressAllowlistConfigEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Select(Map).ToArray();
    }

    private static EgressAllowlistEntry Map(EgressAllowlistConfigEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new EgressAllowlistEntry
        {
            Host = entry.Host,
            HostPattern = entry.HostPattern,
            Schemes = entry.Schemes.ToArray(),
            Ports = entry.Ports.ToArray()
        };
    }
}

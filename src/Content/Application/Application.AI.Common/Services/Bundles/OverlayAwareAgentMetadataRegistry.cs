using Application.AI.Common.Interfaces;
using Domain.AI.Agents;
using Domain.AI.Bundles;

namespace Application.AI.Common.Services.Bundles;

/// <summary>
/// Decorates the host's <see cref="IAgentMetadataRegistry"/> so that, for the duration of a bundle run,
/// the ephemeral agent published on the ambient <see cref="EphemeralAgentOverlay"/> is resolvable
/// <em>ahead of</em> the persistent, startup-discovered agents — without ever being added to them.
/// Outside a bundle run (the overlay is unset) every call forwards straight to the inner registry, so
/// host behaviour is unchanged.
/// </summary>
/// <remarks>
/// The overlay carries a single agent (the one being run). It shadows a persistent agent of the same id
/// for that run only and is invisible to every other async flow, matching the isolation the persistent
/// registries provide for host-installed agents.
/// </remarks>
public sealed class OverlayAwareAgentMetadataRegistry : IAgentMetadataRegistry
{
    private readonly IAgentMetadataRegistry _inner;

    /// <summary>Initialises the decorator over the persistent agent registry.</summary>
    /// <param name="inner">The host's real agent registry, populated by startup discovery.</param>
    public OverlayAwareAgentMetadataRegistry(IAgentMetadataRegistry inner)
    {
        _inner = inner;
    }

    /// <inheritdoc />
    public AgentDefinition? TryGet(string agentId)
    {
        var overlay = EphemeralAgentOverlayAccessor.Current;
        return overlay is not null && overlay.OwnsAgent(agentId) ? overlay.Agent : _inner.TryGet(agentId);
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentDefinition> GetAll() =>
        Merge(_inner.GetAll(), EphemeralAgentOverlayAccessor.Current, overlayMatchesQuery: true);

    /// <inheritdoc />
    public IReadOnlyList<AgentDefinition> GetByCategory(string category)
    {
        var overlay = EphemeralAgentOverlayAccessor.Current;
        return Merge(_inner.GetByCategory(category), overlay,
            overlayMatchesQuery: overlay is not null
                && string.Equals(overlay.Agent.Category, category, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentDefinition> GetByTags(IEnumerable<string> tags)
    {
        var overlay = EphemeralAgentOverlayAccessor.Current;
        if (overlay is null)
            return _inner.GetByTags(tags);

        var tagSet = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        return Merge(_inner.GetByTags(tagSet), overlay, overlay.Agent.Tags.Any(tagSet.Contains));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SearchedPaths => _inner.SearchedPaths;

    /// <summary>
    /// Projects the inner result set with the overlay's shadow applied: the overlay agent replaces any
    /// persistent agent it owns for the whole run, so that shadowed inner entry is dropped from
    /// <em>every</em> projection (not just the ones the overlay happens to match). The overlay agent is
    /// then included only when it satisfies the current query (<paramref name="overlayMatchesQuery"/>),
    /// so a run never sees the stale persistent definition under its old category or tags.
    /// </summary>
    private static IReadOnlyList<AgentDefinition> Merge(
        IReadOnlyList<AgentDefinition> inner, EphemeralAgentOverlay? overlay, bool overlayMatchesQuery)
    {
        if (overlay is null)
            return inner;

        var result = inner.Where(a => !overlay.OwnsAgent(a.Id)).ToList();

        if (overlayMatchesQuery)
            result.Add(overlay.Agent);

        return result;
    }
}

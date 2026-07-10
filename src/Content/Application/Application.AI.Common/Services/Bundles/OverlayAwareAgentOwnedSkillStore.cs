using Application.AI.Common.Interfaces.Skills;
using Domain.AI.Bundles;
using Domain.AI.Skills;

namespace Application.AI.Common.Services.Bundles;

/// <summary>
/// Decorates the host's <see cref="IAgentOwnedSkillStore"/> so that, for the duration of a bundle run,
/// the ephemeral agent's owned skills resolve from the ambient <see cref="EphemeralAgentOverlay"/>
/// <em>ahead of</em> the persistent store — without ever being written into it. Outside a bundle run
/// (the overlay is unset) every call forwards straight to the inner store, so host behaviour is
/// unchanged.
/// </summary>
/// <remarks>
/// Writes (<see cref="Register"/>) always go to the inner store: the overlay is a read-only projection
/// of a staged bundle and startup discovery must never land in it. Reads consult the overlay first, but
/// only when its agent id matches the requested one, so a bundle can shadow nothing it does not own.
/// </remarks>
public sealed class OverlayAwareAgentOwnedSkillStore : IAgentOwnedSkillStore
{
    private readonly IAgentOwnedSkillStore _inner;

    /// <summary>Initialises the decorator over the persistent owned-skill store.</summary>
    /// <param name="inner">The host's real owned-skill store, populated by agent discovery.</param>
    public OverlayAwareAgentOwnedSkillStore(IAgentOwnedSkillStore inner)
    {
        _inner = inner;
    }

    /// <inheritdoc />
    public void Register(string agentId, SkillDefinition skill) => _inner.Register(agentId, skill);

    /// <inheritdoc />
    public SkillDefinition? TryGet(string agentId, string skillId)
    {
        var overlaySkills = OverlaySkillsFor(agentId);

        // When the overlay owns this agent id it is AUTHORITATIVE for the agent's owned skills: resolve
        // only from it and never fall through to the inner store. Otherwise a bundle whose ephemeral
        // agent id collides with a host agent would silently inherit that host agent's private skills.
        // A miss here returns null so the factory falls back to the GLOBAL shared pool — the intended
        // reuse path — not to a colliding agent's owned skills.
        if (overlaySkills is not null)
            return overlaySkills.FirstOrDefault(s => string.Equals(s.Id, skillId, StringComparison.OrdinalIgnoreCase));

        return _inner.TryGet(agentId, skillId);
    }

    /// <inheritdoc />
    public IReadOnlyList<SkillDefinition> GetForAgent(string agentId) =>
        // Authoritative when the overlay owns this agent id (see TryGet); otherwise forward to the inner store.
        OverlaySkillsFor(agentId) ?? _inner.GetForAgent(agentId);

    /// <summary>
    /// Returns the ambient overlay's owned skills when an overlay is active <em>and</em> its agent id
    /// matches <paramref name="agentId"/>; otherwise null (no overlay, or the overlay is for a different
    /// agent).
    /// </summary>
    private static IReadOnlyList<SkillDefinition>? OverlaySkillsFor(string agentId)
    {
        var overlay = EphemeralAgentOverlayAccessor.Current;
        return overlay is not null && overlay.OwnsAgent(agentId) ? overlay.OwnedSkills : null;
    }
}

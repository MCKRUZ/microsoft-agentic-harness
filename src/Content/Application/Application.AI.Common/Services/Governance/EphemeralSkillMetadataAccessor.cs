using Domain.AI.Skills;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Ambient accessor that publishes one <see cref="SkillDefinition"/> for the current async flow —
/// used when a skill's metadata must be visible to Infrastructure's <c>SkillManifestEgressPolicyResolver</c>
/// without ever being written into the shared, permanently-cached <c>ISkillMetadataRegistry</c> (#618).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>ISkillMetadataRegistry</c> is a read-only cache populated once at
/// startup from configured skill directories; it has no write API by design (see its own remarks).
/// A meta-harness eval run needs the egress resolver to see a CANDIDATE skill's own proposed
/// allowlist — a skill that is not, and must never become, a permanent registry entry, since a
/// candidate is a proposed mutation under test, not a real skill installation. Follows the same
/// pattern as this project's other per-flow accessors (<c>CapabilityEnvelopeAccessor</c>,
/// <c>ToolAdmissionAccessor</c>): an <see cref="AsyncLocal{T}"/> the run path sets at the start of
/// one eval task and clears in a <c>finally</c> (via <c>using</c>), read at resolution time. When
/// unset — every non-eval code path — the resolver sees nothing published here and behaves
/// identically to a host with no ephemeral-skill concept.
/// </para>
/// <para>
/// <b>Scoped to exactly one skill</b> (#618, scoped to the common single-skill eval case): an eval
/// run testing a candidate that bundles more than one skill's files together does not get its
/// non-primary skills' egress scoped by this mechanism. Tracked as a follow-up rather than handled
/// here.
/// </para>
/// <para>
/// <b>Concurrency note the read side (Infrastructure's <c>SkillManifestEgressPolicyResolver</c>) must
/// respect.</b> Two eval runs testing different candidates ordinarily active at the same time can
/// legitimately publish the SAME <see cref="SkillDefinition.Id"/> — a candidate is usually a
/// proposed edit to an existing skill, so two candidates under concurrent evaluation are commonly
/// named identically. Because this accessor is <see cref="AsyncLocal{T}"/>-backed, each call's own
/// published value is isolated to its own async flow and never bleeds into another; the caller who
/// changes this class must not add any cross-flow cache keyed on skill id, or that isolation is lost
/// on the write side even though this class itself remains correct.
/// </para>
/// </remarks>
public static class EphemeralSkillMetadataAccessor
{
    private static readonly AsyncLocal<SkillDefinition?> s_current = new();

    /// <summary>
    /// Returns the ephemeral skill definition for the current async flow when its id matches
    /// <paramref name="skillId"/> (case-insensitive, matching every other skill-id comparison in
    /// this subsystem), or <see langword="null"/> when no ephemeral skill is active or the active
    /// one has a different id.
    /// </summary>
    public static SkillDefinition? TryGet(string skillId)
    {
        var current = s_current.Value;
        return current is not null && string.Equals(current.Id, skillId, StringComparison.OrdinalIgnoreCase)
            ? current
            : null;
    }

    /// <summary>
    /// Whether an ephemeral skill definition matching <paramref name="skillId"/> is active on the
    /// current async flow. Used by the resolver to decide whether a resolution must bypass its
    /// shared cache rather than read from it (see the concurrency note on this class).
    /// </summary>
    public static bool HasOverride(string skillId) => TryGet(skillId) is not null;

    /// <summary>
    /// Publishes <paramref name="skill"/> as the ambient ephemeral skill for the current async flow
    /// and returns a handle that restores the previous ambient value when disposed. Use with
    /// <c>using</c> so the value is guaranteed to be torn down when the eval run completes, even on
    /// exception.
    /// </summary>
    public static IDisposable Begin(SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var previous = s_current.Value;
        s_current.Value = skill;
        return new Scope(previous);
    }

    private sealed class Scope(SkillDefinition? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            s_current.Value = previous;
        }
    }
}

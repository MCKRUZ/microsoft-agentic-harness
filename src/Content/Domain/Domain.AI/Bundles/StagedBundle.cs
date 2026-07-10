using Domain.AI.Agents;
using Domain.AI.Skills;
using Domain.Common.Config.AI.Plugins;

namespace Domain.AI.Bundles;

/// <summary>
/// The result of staging an externally-authored agent bundle: an <c>AGENT.md</c> (parsed into
/// <see cref="Agent"/>), its nested <c>SKILL.md</c> files (parsed into <see cref="OwnedSkills"/>), and
/// any plugin manifests, all extracted to an isolated directory on disk under the host's hostile-input
/// guards. The parsed definitions carry file paths that point <em>into</em> <see cref="StagedRootDirectory"/>,
/// so the existing skill-disclosure machinery (which reads Tier 2/3 content from disk) can serve the
/// bundle exactly as it serves a host-installed agent-owned skill.
/// </summary>
/// <remarks>
/// <para>
/// A staged bundle is a value: it describes what was extracted and where, but owns no process resources
/// and performs no I/O. The lifetime of <see cref="StagedRootDirectory"/> on disk is owned by the caller
/// that staged it — the bundle-run job store deletes it when the run handle expires. Nothing here deletes
/// the directory, so holding a <see cref="StagedBundle"/> never has a side effect.
/// </para>
/// <para>
/// The bundle's skills are <em>owned</em> by <see cref="Agent"/> in exactly the sense of an agent-owned
/// nested skill: they are private to this bundle's ephemeral agent and must never enter a global registry.
/// The per-run overlay resolves them owned-first, keyed by <see cref="AgentDefinition.Id"/>.
/// </para>
/// </remarks>
public sealed record StagedBundle
{
    /// <summary>
    /// Opaque identifier for this staged bundle, unique per staging operation. Used to name the
    /// staging subdirectory and, later, to key the run handle. Distinct from
    /// <see cref="AgentDefinition.Id"/>, which comes from the bundle's own <c>AGENT.md</c> and is not
    /// guaranteed unique across bundles.
    /// </summary>
    public required string BundleId { get; init; }

    /// <summary>
    /// Absolute path to the directory the bundle was extracted into — the agent root. The
    /// <c>AGENT.md</c> sits at its top level and nested skills under <c>&lt;root&gt;/skills/</c>. Every
    /// path on <see cref="Agent"/> and <see cref="OwnedSkills"/> is contained within this directory.
    /// </summary>
    public required string StagedRootDirectory { get; init; }

    /// <summary>The agent parsed from the bundle's <c>AGENT.md</c>.</summary>
    public required AgentDefinition Agent { get; init; }

    /// <summary>
    /// The skills parsed from the bundle's nested <c>&lt;root&gt;/skills/*/SKILL.md</c> files. Owned by
    /// <see cref="Agent"/>; empty when the bundle declares no nested skills.
    /// </summary>
    public IReadOnlyList<SkillDefinition> OwnedSkills { get; init; } = [];

    /// <summary>
    /// The plugin manifests parsed from any <c>plugin.json</c> files in the bundle. Empty when the
    /// bundle ships no plugins. Carried through staging for completeness; wiring a bundle's plugins into
    /// its ephemeral agent is a later concern.
    /// </summary>
    public IReadOnlyList<PluginManifest> PluginManifests { get; init; } = [];
}

using Domain.AI.Governance;

namespace Domain.AI.Bundles;

/// <summary>
/// The set of capabilities the host <em>grants</em> to one bundle run for one caller — the tools it may
/// invoke, the MCP servers it may reach, and the ceiling on how autonomously it may act. A bundle
/// declares what it <em>wants</em>; this envelope is what it <em>gets</em>. Anything the bundle requests
/// beyond the envelope is denied, never honoured.
/// </summary>
/// <remarks>
/// <para>
/// The envelope is the load-bearing security boundary for running externally-authored agents: the host
/// is the execution host for code it did not write, so the bundle's self-declared <c>AllowedTools</c> /
/// autonomy / MCP references are treated as <em>requests</em> and this per-caller envelope is the
/// authoritative <em>grant</em>. It is resolved once per run (from the caller's credential) and published
/// ambiently for the duration of that run; the permission gate chain and the tool-chain builder read it
/// to deny out-of-envelope tools (bypass-immune), cap autonomy, and drop non-allowlisted MCP servers.
/// </para>
/// <para>
/// <strong>Fail-closed by construction.</strong> The default value grants nothing — empty tool and MCP
/// allowlists and the most restrictive <see cref="AutonomyLevel"/>. A host that has not configured an
/// envelope for a caller therefore denies everything to that caller's bundle rather than defaulting open.
/// </para>
/// <para>
/// This is a pure value object. The ambient plumbing that publishes it for a run lives in the accessor
/// that carries it, mirroring the host's existing per-turn <c>AsyncLocal</c> accessors.
/// </para>
/// </remarks>
public sealed record CapabilityEnvelope
{
    /// <summary>
    /// The tool names this caller's bundle run may invoke. A tool the bundle requests that is not in this
    /// list is denied. Empty (the default) grants no tools — every tool call is denied.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// The names of host-registered MCP servers this caller's bundle run may reach. Bundles reference
    /// servers by name and never define endpoints, so this list is the whole of a bundle's outbound MCP
    /// surface — closing SSRF-by-construction. Empty (the default) grants no MCP servers.
    /// </summary>
    public IReadOnlyList<string> AllowedMcpServers { get; init; } = [];

    /// <summary>
    /// The highest autonomy tier this caller's bundle run may act under. Enforced as a ceiling: the
    /// effective autonomy is the most restrictive of this value, the host's own graded-autonomy gate, and
    /// each tool's blast radius — this can only tighten, never loosen. Defaults to the most restrictive
    /// tier (<see cref="AutonomyLevel.Restricted"/>) so an unset ceiling forces approval on every action.
    /// </summary>
    /// <remarks>
    /// <strong>Current limitation:</strong> only <see cref="AutonomyLevel.Autonomous"/> lets a granted tool
    /// execute without human sign-off. Because live mid-tool-call approval routing is deferred, the governor
    /// treats a required approval as a fail-closed block — so a <see cref="AutonomyLevel.Supervised"/> or
    /// <see cref="AutonomyLevel.Restricted"/> ceiling currently <em>suspends</em> the bundle's tool use
    /// entirely rather than gating it for approval. This matches how the host's plugin and tier baselines
    /// behave today. A bundle that must do work therefore uses an <see cref="AutonomyLevel.Autonomous"/>
    /// ceiling and relies on <see cref="AllowedTools"/> / <see cref="AllowedMcpServers"/> plus the host's
    /// risk and capability gates for confinement.
    /// </remarks>
    public AutonomyLevel AutonomyCeiling { get; init; } = AutonomyLevel.Restricted;

    /// <summary>
    /// Whether this envelope grants the named tool. Case-insensitive, mirroring how the permission
    /// resolver and tool-chain builder match tool names.
    /// </summary>
    /// <param name="toolName">The tool name being checked.</param>
    public bool GrantsTool(string toolName) => AllowedTools.Contains(toolName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this envelope grants access to the named MCP server. Case-insensitive, mirroring how the
    /// MCP tool provider keys servers by name.
    /// </summary>
    /// <param name="serverName">The MCP server name being checked.</param>
    public bool GrantsMcpServer(string serverName) => AllowedMcpServers.Contains(serverName, StringComparer.OrdinalIgnoreCase);
}

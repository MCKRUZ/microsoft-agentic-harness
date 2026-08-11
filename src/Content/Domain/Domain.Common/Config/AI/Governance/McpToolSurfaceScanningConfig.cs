namespace Domain.Common.Config.AI.Governance;

/// <summary>
/// Posture for the MCP tool <em>surface</em> scan — tool name collision, cross-server shadowing, and
/// definition drift (rug pull) — as distinct from <see cref="GovernanceConfig.EnableMcpSecurity"/>'s
/// per-tool content rules, which this feature reuses the same flag to gate. Bound from
/// <c>AppConfig:AI:Governance:McpToolSurfaceScanning</c> in appsettings.json.
/// </summary>
public sealed class McpToolSurfaceScanningConfig
{
    /// <summary>
    /// Whether a definition-drift finding (a tool's description or schema changed since it was last
    /// seen) withholds the tool until it is re-approved, rather than flagging and continuing.
    /// </summary>
    /// <remarks>
    /// Off by default. A legitimate upstream server update changes descriptions and schemas
    /// routinely, and blocking every such update by default would make this feature something
    /// operators switch off rather than something that protects them. On, a drifted definition is
    /// withheld the same way a tool name collision always is — the difference is that collision is
    /// never a legitimate event and drift usually is.
    /// </remarks>
    public bool StrictDriftMode { get; init; }
}

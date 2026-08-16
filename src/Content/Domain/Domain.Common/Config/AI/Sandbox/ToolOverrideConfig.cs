namespace Domain.Common.Config.AI.Sandbox;

/// <summary>
/// Per-tool permission override from appsettings, merged with a tool's own
/// <c>ITool.RequiredCapabilities</c> declaration.
/// </summary>
/// <remarks>
/// <see cref="DeniedCapabilities"/> is subtracted from the tool's declared
/// <c>RequiredCapabilities</c>, and the resulting profile feeds two different consumers with
/// opposite consequences:
/// <list type="bullet">
/// <item><description><c>CapabilityEnforcer</c> checks the result against the caller's granted
/// capabilities — removing a bit here shrinks what must be granted, so it makes that check
/// <em>more permissive</em>, never more restrictive.</description></item>
/// <item><description><c>DockerSandboxExecutor</c> reads the same bits to set the container's
/// network mode (<c>none</c> without <c>NetworkAccess</c>) and whether the workspace bind mount is
/// read-only (without <c>FileWrite</c>) — there, removing a bit genuinely restricts the tool.
/// Deleting a deny entry re-opens container egress; see
/// <c>ToolPermissionProfileResolver.ParseCapabilities</c>'s remarks.</description></item>
/// </list>
/// It cannot restrict a tool that never runs through the sandbox.
/// </remarks>
public sealed class ToolOverrideConfig
{
    /// <summary>
    /// Capability names to deny (e.g., "NetworkAccess", "Subprocess").
    /// Removed from the tool's declared capabilities via bitwise AND-NOT before the enforcement
    /// check — see the class remarks for why this loosens, not restricts, what is enforced.
    /// </summary>
    public List<string> DeniedCapabilities { get; init; } = [];

    /// <summary>Filesystem paths the tool is allowed to access.</summary>
    public List<string> AllowedPaths { get; init; } = [];

    /// <summary>Filesystem paths explicitly denied.</summary>
    public List<string> DeniedPaths { get; init; } = [];

    /// <summary>Network hosts the tool is allowed to contact.</summary>
    public List<string> AllowedHosts { get; init; } = [];

    /// <summary>Network hosts explicitly denied.</summary>
    public List<string> DeniedHosts { get; init; } = [];

    /// <summary>
    /// Minimum isolation level name (e.g., "Process", "Container").
    /// Takes the higher of the tool's own declared <c>ITool.MinimumIsolation</c> and this override
    /// (never downgrades).
    /// </summary>
    public string? MinimumIsolation { get; init; }

    /// <summary>Per-tool memory limit override in MB. Null uses system default.</summary>
    public int? MemoryLimitMb { get; init; }

    /// <summary>Per-tool CPU time override in seconds. Null uses system default.</summary>
    public double? CpuTimeSeconds { get; init; }

    /// <summary>Per-tool execution timeout override in seconds. Null uses system default.</summary>
    public int? TimeoutSeconds { get; init; }
}

namespace Domain.Common.Config.AI.Sandbox;

/// <summary>
/// Per-tool permission override from appsettings. Merged with compile-time
/// <c>[ToolCapabilityAttribute]</c> using deny-overrides-allow semantics.
/// Overrides can restrict capabilities but never expand beyond the attribute declaration.
/// </summary>
public sealed class ToolOverrideConfig
{
    /// <summary>
    /// Capability names to deny (e.g., "NetworkAccess", "Subprocess").
    /// Removed from the attribute's declared capabilities via bitwise AND-NOT.
    /// </summary>
    public List<string> DeniedCapabilities { get; init; } = [];

    /// <summary>Filesystem paths the tool is allowed to access. Unioned with attribute paths.</summary>
    public List<string> AllowedPaths { get; init; } = [];

    /// <summary>Filesystem paths explicitly denied. Additive with attribute denied paths.</summary>
    public List<string> DeniedPaths { get; init; } = [];

    /// <summary>Network hosts the tool is allowed to contact. Unioned with attribute hosts.</summary>
    public List<string> AllowedHosts { get; init; } = [];

    /// <summary>Network hosts explicitly denied. Additive with attribute denied hosts.</summary>
    public List<string> DeniedHosts { get; init; } = [];

    /// <summary>
    /// Minimum isolation level name (e.g., "Process", "Container").
    /// Takes the higher of attribute and override (never downgrades).
    /// </summary>
    public string? MinimumIsolation { get; init; }

    /// <summary>Per-tool memory limit override in MB. Null uses system default.</summary>
    public int? MemoryLimitMb { get; init; }

    /// <summary>Per-tool CPU time override in seconds. Null uses system default.</summary>
    public double? CpuTimeSeconds { get; init; }

    /// <summary>Per-tool execution timeout override in seconds. Null uses system default.</summary>
    public int? TimeoutSeconds { get; init; }
}

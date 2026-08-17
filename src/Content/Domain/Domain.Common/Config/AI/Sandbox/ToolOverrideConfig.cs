namespace Domain.Common.Config.AI.Sandbox;

/// <summary>
/// Per-tool permission override from appsettings, merged with a tool's own
/// <c>ITool.RequiredCapabilities</c> declaration.
/// </summary>
/// <remarks>
/// <see cref="DeniedCapabilities"/> genuinely restricts the tool (#405): it is kept apart from
/// the tool's own declaration on <c>ToolPermissionProfile.RequiredCapabilities</c> and only
/// narrows what <c>CapabilityEnforcer</c> will grant and what
/// <c>ToolPermissionProfile.EffectiveCapabilities</c> — the value sandbox provisioning reads —
/// resolves to. It cannot restrict a tool that never runs through the sandbox.
/// </remarks>
public sealed class ToolOverrideConfig
{
    /// <summary>
    /// Capability names to deny (e.g., "NetworkAccess", "Subprocess"). Narrows what the tool is
    /// granted and what gets provisioned for it — see the class remarks.
    /// </summary>
    public List<string> DeniedCapabilities { get; init; } = [];

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

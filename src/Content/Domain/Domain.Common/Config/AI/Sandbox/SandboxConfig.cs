namespace Domain.Common.Config.AI.Sandbox;

/// <summary>
/// Strongly-typed configuration for sandbox execution and capability enforcement.
/// Bound to the "Sandbox" section in appsettings.json.
/// </summary>
public sealed class SandboxConfig
{
    /// <summary>Configuration section name in appsettings.json.</summary>
    public const string SectionName = "Sandbox";

    /// <summary>
    /// Capabilities granted to all sessions by default. Follows least-privilege: only
    /// FileRead and LlmInvocation are granted out of the box. Operators must explicitly
    /// grant FileWrite, NetworkAccess, Subprocess, DatabaseWrite, etc. in appsettings.
    /// Uses string names matching <c>ToolCapability</c> enum values.
    /// </summary>
    public List<string> DefaultGrantedCapabilities { get; init; } =
    [
        "FileRead", "LlmInvocation"
    ];

    /// <summary>
    /// Gets or sets the dedicated root directory for process sandbox workspaces.
    /// Each execution creates a unique subdirectory under this root.
    /// Must be an absolute path with restrictive permissions (700/owner-only).
    /// When null, falls back to the system temp directory.
    /// </summary>
    /// <value>Default: null (uses system temp).</value>
    public string? WorkspaceRoot { get; init; }

    /// <summary>
    /// Per-tool permission overrides keyed by tool name.
    /// Overrides can restrict (never expand) compile-time <c>[ToolCapabilityAttribute]</c> declarations.
    /// </summary>
    public Dictionary<string, ToolOverrideConfig> ToolOverrides { get; init; } = new();
}

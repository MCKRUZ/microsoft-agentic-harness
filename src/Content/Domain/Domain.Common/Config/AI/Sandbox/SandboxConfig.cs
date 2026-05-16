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
    /// Capabilities granted to all sessions by default. Operators restrict this
    /// per-environment in appsettings. Uses string names matching <c>ToolCapability</c>
    /// enum values (e.g., "FileRead", "NetworkAccess"). Parsed at runtime by the resolver.
    /// </summary>
    public List<string> DefaultGrantedCapabilities { get; init; } =
    [
        "FileRead", "FileWrite", "NetworkAccess", "Subprocess",
        "EnvRead", "DatabaseRead", "DatabaseWrite", "LlmInvocation"
    ];

    /// <summary>
    /// Per-tool permission overrides keyed by tool name.
    /// Overrides can restrict (never expand) compile-time <c>[ToolCapabilityAttribute]</c> declarations.
    /// </summary>
    public Dictionary<string, ToolOverrideConfig> ToolOverrides { get; init; } = new();
}

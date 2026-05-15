namespace Domain.AI.Sandbox;

/// <summary>
/// Declares a tool's capability requirements and access scoping rules.
/// Deny-overrides-allow semantics: if a path appears in both
/// <see cref="AllowedPaths"/> and <see cref="DeniedPaths"/>, the deny wins.
/// </summary>
public sealed record ToolPermissionProfile
{
    /// <summary>Capabilities the tool requires to execute.</summary>
    public required ToolCapability RequiredCapabilities { get; init; }

    /// <summary>Filesystem paths the tool is allowed to access.</summary>
    public IReadOnlyList<string> AllowedPaths { get; init; } = [];

    /// <summary>Network hosts the tool is allowed to contact.</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    /// <summary>Programs the tool is allowed to spawn as subprocesses.</summary>
    public IReadOnlyList<string> AllowedPrograms { get; init; } = [];

    /// <summary>Filesystem paths explicitly denied (overrides <see cref="AllowedPaths"/>).</summary>
    public IReadOnlyList<string> DeniedPaths { get; init; } = [];

    /// <summary>Network hosts explicitly denied (overrides <see cref="AllowedHosts"/>).</summary>
    public IReadOnlyList<string> DeniedHosts { get; init; } = [];

    /// <summary>
    /// Minimum sandbox isolation level required for this tool.
    /// The capability enforcer will never downgrade below this level.
    /// </summary>
    public SandboxIsolationLevel MinimumIsolation { get; init; } = SandboxIsolationLevel.Process;
}

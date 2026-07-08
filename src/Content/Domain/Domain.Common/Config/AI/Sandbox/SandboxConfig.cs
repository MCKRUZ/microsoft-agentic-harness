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
    /// Gets or sets whether sandbox execution is enabled.
    /// When disabled, both process and container executors refuse to run tools.
    /// </summary>
    /// <value>Default: true.</value>
    public bool Enabled { get; init; } = true;

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

    /// <summary>
    /// Names of host environment variables copied into sandboxed child processes.
    /// The child environment is cleared before launch (closed-by-default) and rebuilt from
    /// this allowlist, so host secrets, tokens, and credentials are not inherited via the
    /// environment. The default set is the minimum a Windows/POSIX child needs to function:
    /// <c>SystemRoot</c> (required by most Win32 APIs), <c>ComSpec</c> and <c>PATHEXT</c>
    /// (command resolution inside cmd), and <c>PATH</c> (executable lookup).
    /// <c>TEMP</c>/<c>TMP</c>/<c>TMPDIR</c> are never copied from the host — the executor
    /// always points them at the disposable per-execution workspace directory.
    /// Additional per-execution values are granted explicitly via
    /// <c>SandboxExecutionRequest.EnvironmentVariables</c>, not by widening this list.
    /// </summary>
    /// <remarks>
    /// This is PARTIAL isolation — environment-level only. The child process runs as the
    /// same OS user with the same token (no privilege drop), so secrets reachable through
    /// the file system or OS APIs remain reachable. Copying <c>PATH</c> verbatim leaks the
    /// host's directory layout and is a binary-planting surface when PATH contains
    /// user-writable directories; remove <c>PATH</c> from this list for tools that do not
    /// resolve executables. For a real security boundary use container isolation
    /// (<c>SandboxIsolationLevel.Container</c>).
    /// </remarks>
    public List<string> ProcessEnvironmentAllowlist { get; init; } =
    [
        "SystemRoot", "ComSpec", "PATHEXT", "PATH"
    ];
}

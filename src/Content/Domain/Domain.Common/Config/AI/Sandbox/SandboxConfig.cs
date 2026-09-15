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
    /// Capabilities granted to all sessions by default. Uses string names matching
    /// <c>ToolCapability</c> enum values.
    /// </summary>
    /// <remarks>
    /// Set to the union of what the harness's own shipped tools declare via
    /// <c>ITool.RequiredCapabilities</c> (#387), so <c>EnforceToolInvocation</c> and bundle runs — the
    /// two paths that make <c>CapabilityEnforcer</c> live — behave identically to the template's
    /// out-of-the-box behaviour before those declarations existed. <c>EnvRead</c> is deliberately
    /// excluded: no shipped tool declares it, and it is the one bit that reads directly off the host
    /// process environment rather than through a scoped service, so it stays an explicit operator
    /// opt-in. A consumer adding a tool that needs a capability outside this set must grant it here
    /// explicitly — that is the closed-by-default model working as intended, not a gap.
    /// </remarks>
    public List<string> DefaultGrantedCapabilities { get; init; } =
    [
        "FileRead", "FileWrite", "NetworkAccess", "Subprocess",
        "DatabaseRead", "DatabaseWrite", "LlmInvocation"
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
    /// Per-tool permission overrides keyed by tool name (case-insensitively — see remarks).
    /// Overrides can restrict (never expand) a tool's own <c>ITool.RequiredCapabilities</c> declaration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Case-insensitive lookup matters because <c>FirstPartyToolLookup</c> resolves the tool name this
    /// dictionary is keyed by case-insensitively (#655) — an operator-authored entry whose casing
    /// differs from the actual registration key must still be found, or the exact defect #655 closed
    /// reopens one dictionary over.
    /// </para>
    /// <para>
    /// <strong>Enforced by a custom <see langword="init"/> accessor, not merely the default value's own
    /// comparer</strong> (altitude finding, #655 follow-up): a plain
    /// <c>= new(StringComparer.OrdinalIgnoreCase)</c> default is silently discarded the moment any code
    /// assigns this property via an object-initializer — <c>new SandboxConfig { ToolOverrides = new()
    /// {...} }</c> constructs a brand-new, DEFAULT-comparer <see cref="Dictionary{TKey,TValue}"/> and
    /// replaces the property's default value wholesale, never inheriting its comparer (verified
    /// directly; this exact idiom is already used throughout this codebase's own tests). Because
    /// <see langword="init"/> accessors can carry a body exactly like <see langword="set"/>, this
    /// accessor rebuilds whatever is assigned into a fresh <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// dictionary — closing the gap at the one choke point every writer (config binding, an
    /// object-initializer, hand construction) passes through, for every reader, not just the ones that
    /// remember to look up case-insensitively themselves.
    /// </para>
    /// </remarks>
    public Dictionary<string, ToolOverrideConfig> ToolOverrides
    {
        get => _toolOverrides;
        init => _toolOverrides = new Dictionary<string, ToolOverrideConfig>(value, StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, ToolOverrideConfig> _toolOverrides = new(StringComparer.OrdinalIgnoreCase);

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

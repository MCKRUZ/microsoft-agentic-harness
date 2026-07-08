namespace Domain.AI.Sandbox;

/// <summary>
/// Encapsulates all inputs needed to execute a tool in a sandboxed environment.
/// </summary>
public sealed record SandboxExecutionRequest
{
    /// <summary>Name of the tool to execute.</summary>
    public required string ToolName { get; init; }

    /// <summary>Serialized input to pass to the tool.</summary>
    public required string Input { get; init; }

    /// <summary>Resource limits to enforce during execution.</summary>
    public required ResourceLimits Limits { get; init; }

    /// <summary>Permission profile controlling allowed capabilities and paths.</summary>
    public required ToolPermissionProfile PermissionProfile { get; init; }

    /// <summary>Maximum wall-clock time before forcibly terminating the tool.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Executable to launch. Falls back to <see cref="ToolName"/> if null.</summary>
    public string? Command { get; init; }

    /// <summary>
    /// Command-line arguments as individual entries. Preferred over <see cref="Arguments"/>
    /// because each entry is passed directly without shell interpretation, preventing injection.
    /// </summary>
    public IReadOnlyList<string>? ArgumentList { get; init; }

    /// <summary>
    /// Command-line arguments as a single string. Deprecated — use <see cref="ArgumentList"/>
    /// to avoid shell metacharacter injection. This property is no longer consumed by
    /// <see cref="ProcessSandboxExecutor"/> and will be removed in a future version.
    /// </summary>
    [Obsolete("Use ArgumentList to avoid command injection. This property is no longer consumed.", error: true)]
    public string? Arguments { get; init; }

    /// <summary>
    /// Optional list of outbound URIs the sandboxed tool intends to reach. When
    /// non-empty, the executor consults the active <c>IEgressPolicy</c> for each
    /// URI BEFORE spawning the process or container; a single deny aborts
    /// execution with a signed failure attestation. Allowed decisions are
    /// recorded into the signed attestation payload so the HMAC manifest
    /// proves which destinations the harness permitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The preflight surface is the sandbox's enforcement seam against egress
    /// policy. An in-process tool that respects the named <c>"egress"</c>
    /// <see cref="HttpClient"/> is already gated by the delegating handler; the
    /// preflight catches subprocess tools that bypass the in-process client by
    /// surfacing the URIs they will visit so the policy can veto BEFORE the
    /// untrusted code runs. A tool that intends to reach destinations it does
    /// not declare here is treated as a policy violation by the egress audit —
    /// the audit captures actual decisions; preflight tags them as
    /// "sandbox.preflight" so dashboards can distinguish them from runtime
    /// allowlist hits.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Uri>? EgressPrecheckTargets { get; init; }

    /// <summary>
    /// Explicit environment variables granted to the sandboxed process for this execution.
    /// The child process environment is cleared and rebuilt from the configured host
    /// allowlist (<c>SandboxConfig.ProcessEnvironmentAllowlist</c>) plus these grants —
    /// nothing else from the host environment is visible to the tool. Grants are applied
    /// last and may override allowlisted values. Null or empty means no extra variables.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
}

namespace Domain.AI.Sandbox;

/// <summary>
/// Encapsulates the inputs needed to start a long-lived, bidirectional sandboxed session — the
/// duplex counterpart to <see cref="SandboxExecutionRequest"/>. Carries no <c>Input</c>: a
/// session's input is a stream the caller writes to over its lifetime, not a single buffered
/// payload known up front. See #371.
/// </summary>
public sealed record SandboxSessionRequest
{
    /// <summary>Name of the tool/server this session runs, used for logging and error messages.</summary>
    public required string ToolName { get; init; }

    /// <summary>Resource limits to enforce for the lifetime of the session.</summary>
    public required ResourceLimits Limits { get; init; }

    /// <summary>Permission profile controlling allowed capabilities and paths.</summary>
    public required ToolPermissionProfile PermissionProfile { get; init; }

    /// <summary>
    /// Maximum wall-clock lifetime of the session before it is forcibly terminated, regardless
    /// of whether the caller is still using it. Unlike <see cref="SandboxExecutionRequest.Timeout"/>,
    /// this is a ceiling on an open-ended conversation, not a completion deadline — a caller that
    /// needs a shorter working lifetime should dispose the session itself once it is done.
    /// </summary>
    public TimeSpan MaxSessionDuration { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Executable to launch. Falls back to <see cref="ToolName"/> if null.</summary>
    public string? Command { get; init; }

    /// <summary>
    /// Command-line arguments as individual entries — never shell-interpreted, matching
    /// <see cref="SandboxExecutionRequest.ArgumentList"/>.
    /// </summary>
    public IReadOnlyList<string>? ArgumentList { get; init; }

    /// <summary>
    /// Optional list of outbound URIs the sandboxed session intends to reach, evaluated against
    /// the active egress policy before the session starts. Same preflight-only semantics as
    /// <see cref="SandboxExecutionRequest.EgressPrecheckTargets"/> — it does not constrain
    /// sockets the running session opens for itself once started.
    /// </summary>
    public IReadOnlyList<Uri>? EgressPrecheckTargets { get; init; }

    /// <summary>
    /// Explicit environment variables granted to the sandboxed session, subject to the same
    /// reserved-name rejection as <see cref="SandboxExecutionRequest.EnvironmentVariables"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    /// <summary>
    /// Container image to run the session in, overriding the sandbox's configured default image.
    /// <strong>Docker-tier only</strong> — a factory for any other isolation level ignores this.
    /// Still validated against <c>ContainerSandboxOptions.AllowedImagePrefixes</c> exactly like the
    /// default image; a caller cannot use this to escape the operator's image allowlist, only to pick
    /// among images the operator has already permitted. Null uses the sandbox's configured default.
    /// </summary>
    public string? ContainerImage { get; init; }

    /// <summary>
    /// Absolute path to a directory on the host whose contents are copied into the session's sandbox
    /// workspace before it starts. <strong>Docker-tier only</strong> — a factory for any other
    /// isolation level must refuse a request that sets this rather than silently ignore it, since the
    /// process tier's workspace has no equivalent containment for caller-chosen content (see
    /// <see cref="ToolPermissionProfile.MinimumIsolation"/>'s remarks on why untrusted, caller-supplied
    /// commands require <see cref="SandboxIsolationLevel.Container"/>). Deliberately a copy contract,
    /// not a bind-mount contract: the caller's source directory may be deleted by something outside
    /// this session's lifetime (e.g. a bundle's staging directory on handle eviction), and a session
    /// that depended on that directory staying mounted for its own duration would race that deletion.
    /// Null starts with an empty workspace, as before this property existed.
    /// </summary>
    public string? WorkspaceSeedDirectory { get; init; }
}

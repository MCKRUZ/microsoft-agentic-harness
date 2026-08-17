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
}

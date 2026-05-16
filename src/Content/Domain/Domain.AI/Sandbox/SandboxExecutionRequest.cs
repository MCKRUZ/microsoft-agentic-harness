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

    /// <summary>Command-line arguments for the subprocess.</summary>
    public string? Arguments { get; init; }
}

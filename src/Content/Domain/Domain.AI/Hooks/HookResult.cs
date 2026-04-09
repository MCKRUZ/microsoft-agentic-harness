namespace Domain.AI.Hooks;

/// <summary>
/// The outcome of a hook execution. Controls whether the pipeline continues
/// and optionally modifies tool inputs or outputs.
/// </summary>
public sealed record HookResult
{
    /// <summary>Whether the pipeline should continue. Default is true. False blocks execution.</summary>
    public bool Continue { get; init; } = true;

    /// <summary>When true, suppresses the hook's stdout from being shown to the user.</summary>
    public bool SuppressOutput { get; init; }

    /// <summary>
    /// Modified tool input parameters. Only applies to PreToolUse hooks.
    /// When set, replaces the original tool parameters.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? ModifiedInput { get; init; }

    /// <summary>
    /// Modified tool output. Only applies to PostToolUse hooks.
    /// When set, replaces the original tool output.
    /// </summary>
    public string? ModifiedOutput { get; init; }

    /// <summary>Additional context to inject into the conversation.</summary>
    public string? AdditionalContext { get; init; }

    /// <summary>
    /// Human-readable reason when Continue is false (blocking).
    /// </summary>
    public string? StopReason { get; init; }

    /// <summary>Creates a default pass-through result.</summary>
    public static HookResult PassThrough() => new();

    /// <summary>Creates a blocking result with a reason.</summary>
    public static HookResult Block(string reason) => new() { Continue = false, StopReason = reason };
}

namespace Domain.Common.Config.AI.Hooks;

/// <summary>
/// Configuration for the hook system that executes user-defined callbacks
/// at key points in the agent lifecycle. Bound from <c>AppConfig:AI:Hooks</c>
/// in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Hooks run at defined lifecycle points (PreToolUse, PostToolUse, PreMessage, PostMessage, etc.).
/// Each hook executes within <see cref="DefaultTimeoutMs"/> and up to <see cref="MaxParallelHooks"/>
/// hooks can run concurrently for the same lifecycle event.
/// </para>
/// </remarks>
public class HooksConfig
{
    /// <summary>
    /// Gets or sets whether the hook system is enabled. When <c>false</c>,
    /// all hook registrations are ignored and no hooks execute.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the default timeout in milliseconds for a single hook execution.
    /// Hooks exceeding this timeout are cancelled and logged as failures.
    /// </summary>
    public int DefaultTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Gets or sets the maximum number of hooks that can execute concurrently
    /// for a single lifecycle event.
    /// </summary>
    public int MaxParallelHooks { get; set; } = 10;
}

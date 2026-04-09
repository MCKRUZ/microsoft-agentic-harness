namespace Domain.AI.Hooks;

/// <summary>
/// Defines a hook -- a callback that executes in response to a lifecycle event.
/// Hooks can modify tool inputs/outputs, inject context, or block execution.
/// </summary>
public sealed record HookDefinition
{
    /// <summary>Unique identifier for this hook registration.</summary>
    public required string Id { get; init; }

    /// <summary>The event this hook subscribes to.</summary>
    public required HookEvent Event { get; init; }

    /// <summary>The execution mechanism for this hook.</summary>
    public required HookType Type { get; init; }

    /// <summary>
    /// Optional glob pattern to filter which tools this hook applies to.
    /// Only relevant for tool lifecycle events (PreToolUse, PostToolUse).
    /// Null means match all tools.
    /// </summary>
    public string? ToolMatcher { get; init; }

    /// <summary>Timeout in milliseconds for this hook's execution. Default is 5000ms.</summary>
    public int TimeoutMs { get; init; } = 5000;

    /// <summary>When true, the hook is automatically unregistered after its first execution.</summary>
    public bool RunOnce { get; init; }

    /// <summary>Execution priority. Lower values execute first. Default is 100.</summary>
    public int Priority { get; init; } = 100;

    /// <summary>For Command hooks: the shell command line to execute.</summary>
    public string? CommandLine { get; init; }

    /// <summary>For Prompt hooks: the template string to evaluate against the context.</summary>
    public string? PromptTemplate { get; init; }

    /// <summary>For Middleware hooks: the fully qualified type name to resolve from DI.</summary>
    public string? MiddlewareTypeName { get; init; }

    /// <summary>For Http hooks: the webhook URL to POST the context to.</summary>
    public string? WebhookUrl { get; init; }
}

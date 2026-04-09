namespace Domain.AI.Hooks;

/// <summary>
/// Contextual information passed to hooks during execution.
/// Contains details about the triggering event -- tool name, parameters, agent state, etc.
/// </summary>
public sealed record HookExecutionContext
{
    /// <summary>The event that triggered this hook execution.</summary>
    public required HookEvent Event { get; init; }

    /// <summary>The agent ID in whose context this hook is firing, if applicable.</summary>
    public string? AgentId { get; init; }

    /// <summary>The tool name for tool lifecycle events.</summary>
    public string? ToolName { get; init; }

    /// <summary>The tool parameters for PreToolUse events.</summary>
    public IReadOnlyDictionary<string, object?>? ToolParameters { get; init; }

    /// <summary>The tool result output for PostToolUse events.</summary>
    public string? ToolResult { get; init; }

    /// <summary>The conversation turn number, if applicable.</summary>
    public int? TurnNumber { get; init; }

    /// <summary>The conversation/session ID, if applicable.</summary>
    public string? ConversationId { get; init; }
}

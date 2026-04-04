namespace Application.Common.Logging;

/// <summary>
/// Immutable scope descriptor for agent execution context, used with
/// <c>ILogger.BeginScope</c> to propagate agentic identity through
/// the logging pipeline.
/// </summary>
/// <remarks>
/// When pushed onto the scope stack via <c>logger.BeginScope(new AgentLogScope(...))</c>,
/// all logging providers that consume <c>IExternalScopeProvider</c> automatically receive
/// these fields — including <c>AgentConsoleFormatter</c>, <c>StructuredJsonLogger</c>,
/// and <c>AgentFileLoggerProvider</c>.
/// <para>
/// The scope hierarchy mirrors the agentic execution model:
/// <code>
/// Agent (AgentId, ParentAgentId)
///   └── Conversation (ConversationId)
///         └── Turn (TurnNumber)
///               └── Tool Invocation (ToolName)
/// </code>
/// </para>
/// Scopes are nested via multiple <c>BeginScope</c> calls. Inner scopes
/// inherit outer scope properties automatically.
/// </remarks>
/// <example>
/// <code>
/// using (logger.BeginScope(new AgentLogScope(AgentId: "planner")))
/// {
///     using (logger.BeginScope(new AgentLogScope(TurnNumber: 3)))
///     {
///         using (logger.BeginScope(new AgentLogScope(ToolName: "file_system")))
///         {
///             logger.LogInformation("Reading file {Path}", filePath);
///             // All three scope levels are visible to formatters/providers
///         }
///     }
/// }
/// </code>
/// </example>
/// <param name="AgentId">The agent's unique identifier (e.g., "planner", "code-reviewer").</param>
/// <param name="ParentAgentId">The parent agent's identifier, if this is a subagent.</param>
/// <param name="ConversationId">The conversation or session identifier.</param>
/// <param name="TurnNumber">The current conversation turn number.</param>
/// <param name="ToolName">The tool currently being executed, if any.</param>
public record AgentLogScope(
    string? AgentId = null,
    string? ParentAgentId = null,
    string? ConversationId = null,
    int? TurnNumber = null,
    string? ToolName = null)
{
    /// <inheritdoc />
    public override string ToString()
    {
        var parts = new List<string>(5);

        if (AgentId is not null)
            parts.Add($"Agent={AgentId}");
        if (ParentAgentId is not null)
            parts.Add($"Parent={ParentAgentId}");
        if (ConversationId is not null)
            parts.Add($"Conv={ConversationId}");
        if (TurnNumber is not null)
            parts.Add($"Turn={TurnNumber}");
        if (ToolName is not null)
            parts.Add($"Tool={ToolName}");

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Yields the non-null properties as key-value pairs for structured logging output.
    /// Keys use camelCase to match JSON serialization conventions.
    /// </summary>
    public IEnumerable<KeyValuePair<string, object?>> ToProperties()
    {
        if (AgentId is not null) yield return new("agentId", AgentId);
        if (ParentAgentId is not null) yield return new("parentAgentId", ParentAgentId);
        if (ConversationId is not null) yield return new("conversationId", ConversationId);
        if (TurnNumber is not null) yield return new("turnNumber", TurnNumber);
        if (ToolName is not null) yield return new("toolName", ToolName);
    }
}

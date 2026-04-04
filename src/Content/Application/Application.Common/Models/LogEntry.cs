using System.Collections.Immutable;
using Application.Common.Logging;
using Microsoft.Extensions.Logging;

namespace Application.Common.Models;

/// <summary>
/// Represents a structured log entry used by <c>CallbackLoggerProvider</c>
/// and <c>InMemoryRingBufferLoggerProvider</c> to expose log data as
/// first-class objects rather than raw strings.
/// </summary>
/// <remarks>
/// This record captures all the information available at log time including
/// scope data from <c>AgentScopeProvider</c>. It enables typed consumption
/// of log entries by callbacks, diagnostics endpoints, and analysis tools.
/// </remarks>
public record LogEntry
{
    /// <summary>Gets the UTC timestamp when the log entry was created.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Gets the log level (Trace, Debug, Information, Warning, Error, Critical).</summary>
    public required LogLevel Level { get; init; }

    /// <summary>Gets the logger category (typically the fully-qualified class name).</summary>
    public required string Category { get; init; }

    /// <summary>Gets the event ID associated with this log entry.</summary>
    public EventId EventId { get; init; }

    /// <summary>Gets the formatted log message.</summary>
    public required string Message { get; init; }

    /// <summary>Gets the exception associated with this log entry, if any.</summary>
    public Exception? Exception { get; init; }

    /// <summary>Gets the agent ID from the current scope, if logging within an agent context.</summary>
    public string? AgentId { get; init; }

    /// <summary>Gets the parent agent ID from the current scope, if this is a subagent.</summary>
    public string? ParentAgentId { get; init; }

    /// <summary>Gets the conversation ID from the current scope, if available.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Gets the conversation turn number from the current scope, if available.</summary>
    public int? TurnNumber { get; init; }

    /// <summary>Gets the tool name from the current scope, if logging during tool execution.</summary>
    public string? ToolName { get; init; }

    /// <summary>Gets any additional scope properties captured at log time.</summary>
    public IReadOnlyDictionary<string, object?> ScopeProperties { get; init; } =
        ImmutableDictionary<string, object?>.Empty;

    /// <summary>
    /// Creates a <see cref="LogEntry"/> from the current logging context, extracting
    /// agent scope data from the provided scope provider. Centralizes entry construction
    /// to eliminate duplication across logger implementations.
    /// </summary>
    /// <param name="logLevel">The log level.</param>
    /// <param name="category">The logger category.</param>
    /// <param name="eventId">The event ID.</param>
    /// <param name="message">The formatted message.</param>
    /// <param name="exception">The exception, if any.</param>
    /// <param name="scopeProvider">The scope provider for agent context extraction.</param>
    /// <returns>A fully populated <see cref="LogEntry"/>.</returns>
    public static LogEntry CreateFromScope(
        LogLevel logLevel,
        string category,
        EventId eventId,
        string message,
        Exception? exception,
        IExternalScopeProvider? scopeProvider)
    {
        var agentScope = AgentScopeProvider.GetCurrentAgentScope(scopeProvider);

        return new LogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = logLevel,
            Category = category,
            EventId = eventId,
            Message = message,
            Exception = exception,
            AgentId = agentScope?.AgentId,
            ParentAgentId = agentScope?.ParentAgentId,
            ConversationId = agentScope?.ConversationId,
            TurnNumber = agentScope?.TurnNumber,
            ToolName = agentScope?.ToolName
        };
    }
}

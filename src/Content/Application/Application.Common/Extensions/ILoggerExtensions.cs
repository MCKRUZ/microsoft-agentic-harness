using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Application.Common.Extensions;

/// <summary>
/// Extension methods for <see cref="ILogger"/> providing structured logging patterns
/// for agent orchestration scenarios. All methods include trace correlation via
/// <see cref="Activity.Current"/>.
/// </summary>
/// <remarks>
/// These extensions are transport-agnostic (no HttpContext dependency). For HTTP-specific
/// logging, see the Presentation layer's exception filter extensions.
/// </remarks>
public static class ILoggerExtensions
{
    private const int MaxLogFieldLength = 200;
    private const string NoValue = "N/A";
    private const string NoTrace = "no-trace";

    /// <summary>Operations exceeding this duration are logged at Warning level.</summary>
    private static readonly TimeSpan SlowOperationThreshold = TimeSpan.FromSeconds(5);

    #region Tool Execution

    /// <summary>
    /// Logs a tool execution result with duration and optional summary.
    /// Warns on failure, Info on success.
    /// </summary>
    public static void LogToolExecution(
        this ILogger logger,
        string toolName,
        TimeSpan duration,
        bool succeeded,
        string? resultSummary = null)
    {
        var level = succeeded ? LogLevel.Information : LogLevel.Warning;

        logger.Log(level,
            "Tool '{ToolName}' completed in {DurationMs:F1}ms. Succeeded: {Succeeded}. Result: {Result} | TraceId: {TraceId}",
            toolName,
            duration.TotalMilliseconds,
            succeeded,
            resultSummary?.Truncate(MaxLogFieldLength) ?? NoValue,
            GetTraceId());
    }

    #endregion

    #region Agent Turn

    /// <summary>
    /// Logs an agent turn boundary with optional token usage.
    /// </summary>
    public static void LogAgentTurn(
        this ILogger logger,
        string agentId,
        int turnNumber,
        long? tokensUsed = null)
    {
        logger.LogInformation(
            "Agent '{AgentId}' completing turn {TurnNumber}. Tokens: {TokensUsed} | TraceId: {TraceId}",
            agentId,
            turnNumber,
            (object?)tokensUsed ?? NoValue,
            GetTraceId());
    }

    #endregion

    #region Content Safety

    /// <summary>
    /// Logs a content safety screening event. Warns when content is blocked.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="category">Safety category (e.g., "hate", "violence", "pii").</param>
    /// <param name="action">Action taken (e.g., "passed", "blocked", "flagged").</param>
    /// <param name="detail">Optional detail about the screening result.</param>
    public static void LogContentSafetyEvent(
        this ILogger logger,
        string category,
        string action,
        string? detail = null)
    {
        var level = string.Equals(action, "blocked", StringComparison.OrdinalIgnoreCase)
            ? LogLevel.Warning
            : LogLevel.Information;

        logger.Log(level,
            "Content safety [{Category}]: {Action}. Detail: {Detail} | TraceId: {TraceId}",
            category,
            action,
            detail?.Truncate(MaxLogFieldLength) ?? NoValue,
            GetTraceId());
    }

    #endregion

    #region Performance

    /// <summary>
    /// Logs performance information for an operation. Warns if duration exceeds
    /// <see cref="SlowOperationThreshold"/>.
    /// </summary>
    public static void LogPerformance(
        this ILogger logger,
        string operation,
        TimeSpan duration,
        string? context = null)
    {
        var level = duration > SlowOperationThreshold ? LogLevel.Warning : LogLevel.Information;

        logger.Log(level,
            "Operation '{Operation}' completed in {DurationMs:F1}ms. Context: {Context}",
            operation,
            duration.TotalMilliseconds,
            context ?? NoValue);
    }

    #endregion

    #region Agent Events

    /// <summary>
    /// Logs a structured agent event with optional key-value data.
    /// </summary>
    public static void LogAgentEvent(
        this ILogger logger,
        string eventName,
        IDictionary<string, object>? data = null,
        string? context = null)
    {
        logger.LogInformation(
            "Agent event: {EventName} | Context: {Context} | Data: {Data}",
            eventName,
            context ?? NoValue,
            data is not null
                ? string.Join(", ", data.Select(kv => $"{kv.Key}={kv.Value}"))
                : NoValue);
    }

    #endregion

    #region Error

    /// <summary>
    /// Logs an exception with operation context and trace correlation.
    /// For non-HTTP scenarios: agent loops, tool execution, background tasks.
    /// </summary>
    public static void LogErrorWithContext(
        this ILogger logger,
        Exception exception,
        string operation,
        string? additionalContext = null)
    {
        logger.LogError(exception,
            "Exception in operation '{Operation}'. Context: {ContextInfo} | TraceId: {TraceId}",
            operation,
            additionalContext ?? NoValue,
            GetTraceId());
    }

    #endregion

    #region MCP

    /// <summary>
    /// Logs an MCP server or client operation.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="direction">Direction of the call: "inbound" (server) or "outbound" (client).</param>
    /// <param name="operationType">MCP operation (e.g., "tools/list", "tools/call", "resources/read").</param>
    /// <param name="serverName">Optional MCP server name.</param>
    /// <param name="duration">Optional operation duration.</param>
    public static void LogMcpOperation(
        this ILogger logger,
        string direction,
        string operationType,
        string? serverName = null,
        TimeSpan? duration = null)
    {
        logger.LogInformation(
            "MCP {Direction} [{OperationType}] Server: {ServerName}. Duration: {DurationMs} | TraceId: {TraceId}",
            direction,
            operationType,
            serverName ?? NoValue,
            duration?.TotalMilliseconds.ToString("F1") ?? NoValue,
            GetTraceId());
    }

    #endregion

    #region Private Helpers

    private static string GetTraceId() =>
        Activity.Current?.Id ?? NoTrace;

    #endregion
}

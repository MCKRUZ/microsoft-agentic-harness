using System.Diagnostics;
using Domain.Common.Extensions;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Extensions;

/// <summary>
/// Extension methods for <see cref="ILogger"/> providing structured logging patterns
/// for AI agent orchestration scenarios. All methods include trace correlation via
/// <see cref="Activity.Current"/>.
/// </summary>
/// <remarks>
/// These are AI-specific logging extensions. For generic operation logging,
/// see <see cref="Application.Common.Extensions.ILoggerExtensions"/>.
/// </remarks>
public static class ILoggerAgentExtensions
{
    private const int MaxLogFieldLength = 200;
    private const string NoValue = "N/A";
    private const string NoTrace = "no-trace";

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

    #region MCP

    /// <summary>
    /// Logs an MCP server or client operation.
    /// </summary>
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

    #region Agent Events

    /// <summary>
    /// Logs a structured agent event with optional key-value data.
    /// </summary>
    public static void LogAgentEvent(
        this ILogger logger,
        string eventName,
        IReadOnlyDictionary<string, object>? data = null,
        string? context = null)
    {
        logger.LogInformation(
            "Agent event: {EventName} | Context: {Context} | Data: {Data} | TraceId: {TraceId}",
            eventName,
            context ?? NoValue,
            data is not null
                ? string.Join(", ", data.Select(kv => $"{kv.Key}={kv.Value}"))
                : NoValue,
            GetTraceId());
    }

    #endregion

    private static string GetTraceId() =>
        Activity.Current?.Id ?? NoTrace;
}

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Application.Common.Extensions;

/// <summary>
/// Extension methods for <see cref="ILogger"/> providing structured logging patterns
/// for general operation scenarios. All methods include trace correlation via
/// <see cref="Activity.Current"/>.
/// </summary>
/// <remarks>
/// These extensions are transport-agnostic (no HttpContext dependency). For AI agent-specific
/// logging extensions, see <c>Application.AI.Common.Extensions.ILoggerAgentExtensions</c>.
/// </remarks>
public static class ILoggerExtensions
{
    private const int MaxLogFieldLength = 200;
    private const string NoValue = "N/A";
    private const string NoTrace = "no-trace";

    /// <summary>Operations exceeding this duration are logged at Warning level.</summary>
    private static readonly TimeSpan SlowOperationThreshold = TimeSpan.FromSeconds(5);

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

    #region Private Helpers

    private static string GetTraceId() =>
        Activity.Current?.Id ?? NoTrace;

    #endregion
}

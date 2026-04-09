namespace Infrastructure.AI.Tools;

/// <summary>
/// Classifies tool execution exceptions into telemetry-safe error category strings.
/// Used by <see cref="BatchedToolExecutionStrategy"/> to populate
/// <see cref="Application.AI.Common.Models.Tools.ToolExecutionResult.ErrorCategory"/>
/// without leaking implementation details into telemetry.
/// </summary>
public static class ToolErrorClassifier
{
    /// <summary>
    /// Maps an exception to a telemetry-safe error category string.
    /// </summary>
    /// <param name="ex">The exception thrown during tool execution.</param>
    /// <returns>A short, stable string suitable for metrics grouping and alerting.</returns>
    public static string Classify(Exception ex) => ex switch
    {
        OperationCanceledException => "timeout",
        UnauthorizedAccessException => "permission_denied",
        FileNotFoundException => "not_found",
        KeyNotFoundException => "not_found",
        ArgumentException => "invalid_input",
        _ => "internal_error"
    };
}

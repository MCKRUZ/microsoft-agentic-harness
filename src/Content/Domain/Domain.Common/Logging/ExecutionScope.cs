namespace Domain.Common.Logging;

/// <summary>
/// Immutable scope descriptor for execution context, used with
/// <c>ILogger.BeginScope</c> to propagate executor identity through
/// the logging pipeline.
/// </summary>
public record ExecutionScope(
    string? ExecutorId = null,
    string? ParentExecutorId = null,
    string? CorrelationId = null,
    int? StepNumber = null,
    string? OperationName = null)
{
    /// <inheritdoc />
    public override string ToString()
    {
        var parts = new List<string>(5);

        if (ExecutorId is not null)
            parts.Add($"Executor={ExecutorId}");
        if (ParentExecutorId is not null)
            parts.Add($"Parent={ParentExecutorId}");
        if (CorrelationId is not null)
            parts.Add($"Corr={CorrelationId}");
        if (StepNumber is not null)
            parts.Add($"Step={StepNumber}");
        if (OperationName is not null)
            parts.Add($"Op={OperationName}");

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Yields the non-null properties as key-value pairs for structured logging output.
    /// </summary>
    public IEnumerable<KeyValuePair<string, object?>> ToProperties()
    {
        if (ExecutorId is not null) yield return new("executorId", ExecutorId);
        if (ParentExecutorId is not null) yield return new("parentExecutorId", ParentExecutorId);
        if (CorrelationId is not null) yield return new("correlationId", CorrelationId);
        if (StepNumber is not null) yield return new("stepNumber", StepNumber);
        if (OperationName is not null) yield return new("operationName", OperationName);
    }
}

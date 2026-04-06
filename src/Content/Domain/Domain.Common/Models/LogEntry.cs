using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Domain.Common.Models;

/// <summary>
/// Represents a structured log entry exposing log data as a first-class object
/// rather than a raw string. Used by callback and ring-buffer logging providers.
/// </summary>
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

    /// <summary>Gets the executor ID from the current scope, if logging within an execution context.</summary>
    public string? ExecutorId { get; init; }

    /// <summary>Gets the parent executor ID from the current scope, if this is a sub-executor.</summary>
    public string? ParentExecutorId { get; init; }

    /// <summary>Gets the correlation ID from the current scope, if available.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Gets the step number from the current scope, if available.</summary>
    public int? StepNumber { get; init; }

    /// <summary>Gets the operation name from the current scope, if logging during an operation.</summary>
    public string? OperationName { get; init; }

    /// <summary>Gets any additional scope properties captured at log time.</summary>
    public IReadOnlyDictionary<string, object?> ScopeProperties { get; init; } =
        ImmutableDictionary<string, object?>.Empty;
}

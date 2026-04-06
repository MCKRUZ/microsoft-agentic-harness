using Domain.Common.Logging;
using Domain.Common.Models;
using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// Factory for creating <see cref="LogEntry"/> instances from the current logging scope.
/// Centralizes entry construction to eliminate duplication across logger implementations.
/// </summary>
public static class LogEntryFactory
{
    /// <summary>
    /// Creates a <see cref="LogEntry"/> from the current logging context, extracting
    /// execution scope data from the provided scope provider.
    /// </summary>
    public static LogEntry CreateFromScope(
        LogLevel logLevel,
        string category,
        EventId eventId,
        string message,
        Exception? exception,
        IExternalScopeProvider? scopeProvider)
    {
        var scope = ExecutionScopeProvider.GetCurrentScope(scopeProvider);

        return new LogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = logLevel,
            Category = category,
            EventId = eventId,
            Message = message,
            Exception = exception,
            ExecutorId = scope?.ExecutorId,
            ParentExecutorId = scope?.ParentExecutorId,
            CorrelationId = scope?.CorrelationId,
            StepNumber = scope?.StepNumber,
            OperationName = scope?.OperationName
        };
    }
}

using Domain.Common.Models;
using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// An <see cref="ILogger"/> implementation that creates <see cref="LogEntry"/> objects
/// and passes them to a user-supplied callback delegate. Designed for SDK consumers
/// who want to handle log entries without implementing <see cref="ILoggerProvider"/>.
/// </summary>
/// <remarks>
/// The callback is invoked synchronously on the logging thread. Keep callback
/// implementations fast and non-blocking to avoid degrading application performance.
/// </remarks>
public sealed class CallbackLogger : ILogger
{
    private readonly string _category;
    private readonly Action<LogEntry> _callback;
    private readonly IExternalScopeProvider? _scopeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CallbackLogger"/> class.
    /// </summary>
    /// <param name="category">The logger category.</param>
    /// <param name="callback">The delegate invoked for each log entry.</param>
    /// <param name="scopeProvider">Optional scope provider for capturing agent context.</param>
    public CallbackLogger(
        string category,
        Action<LogEntry> callback,
        IExternalScopeProvider? scopeProvider)
    {
        _category = category;
        _callback = callback;
        _scopeProvider = scopeProvider;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        _scopeProvider?.Push(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        var entry = LogEntryFactory.CreateFromScope(logLevel, _category, eventId, message, exception, _scopeProvider);

        try
        {
            _callback(entry);
        }
        catch (Exception)
        {
            // Swallow callback exceptions to prevent logging failures
            // from crashing the application
        }
    }
}

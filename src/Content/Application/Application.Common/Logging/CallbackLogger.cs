using Domain.Common.Models;
using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// An <see cref="ILogger"/> implementation that creates <see cref="LogEntry"/> objects
/// and passes them to a user-supplied callback delegate. Designed for SDK consumers
/// who want to handle log entries without implementing <see cref="ILoggerProvider"/>.
/// </summary>
public sealed class CallbackLogger : BaseLogger
{
    private readonly Action<LogEntry> _callback;

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
        : base(category, scopeProvider)
    {
        _callback = callback;
    }

    /// <inheritdoc />
    public override void Log<TState>(
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

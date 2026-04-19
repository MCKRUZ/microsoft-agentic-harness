using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// An <see cref="ILogger"/> implementation that formats log entries and queues them
/// to a <see cref="NamedPipeLoggerProvider"/> for real-time streaming to connected clients.
/// </summary>
public sealed class NamedPipeLogger : BaseLogger
{
    private readonly NamedPipeLoggerProvider _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="NamedPipeLogger"/> class.
    /// </summary>
    /// <param name="category">The logger category.</param>
    /// <param name="provider">The provider managing the named pipe server.</param>
    /// <param name="scopeProvider">Optional scope provider for agent context propagation.</param>
    public NamedPipeLogger(string category, NamedPipeLoggerProvider provider, IExternalScopeProvider? scopeProvider)
        : base(category, scopeProvider)
    {
        _provider = provider;
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
        var cleanMessage = LoggingHelper.ExtractMessageFromFormatted(message);
        var timestamp = DateTimeOffset.UtcNow;
        var shortLevel = LoggingHelper.GetShortLevel(logLevel);
        var shortCategory = LoggingHelper.GetShortCategory(_category);

        var formatted = $"{timestamp:HH:mm:ss.fff} {shortLevel} [{shortCategory}] {cleanMessage}";

        if (exception is not null)
            formatted += Environment.NewLine + exception;

        _provider.WriteMessage(formatted);
    }
}

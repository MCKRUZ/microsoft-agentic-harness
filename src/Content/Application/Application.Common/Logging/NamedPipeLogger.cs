using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// An <see cref="ILogger"/> implementation that formats log entries and queues them
/// to a <see cref="NamedPipeLoggerProvider"/> for real-time streaming to connected clients.
/// </summary>
/// <remarks>
/// Useful for development-time debugging: run a pipe viewer in a separate terminal
/// to watch agent execution in real-time without tailing log files.
/// </remarks>
public sealed class NamedPipeLogger : ILogger
{
    private readonly string _category;
    private readonly NamedPipeLoggerProvider _provider;
    private readonly IExternalScopeProvider? _scopeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="NamedPipeLogger"/> class.
    /// </summary>
    /// <param name="category">The logger category.</param>
    /// <param name="provider">The provider managing the named pipe server.</param>
    /// <param name="scopeProvider">Optional scope provider for agent context propagation.</param>
    public NamedPipeLogger(string category, NamedPipeLoggerProvider provider, IExternalScopeProvider? scopeProvider)
    {
        _category = category;
        _provider = provider;
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

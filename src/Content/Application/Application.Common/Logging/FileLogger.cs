using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// An <see cref="ILogger"/> implementation that formats log entries and queues them
/// to a <see cref="FileLoggerProvider"/> for background file writing.
/// Produces dual-format output: structured for <c>log.txt</c> and human-readable for <c>console.txt</c>.
/// </summary>
public sealed class FileLogger : BaseLogger
{
    private readonly FileLoggerProvider _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileLogger"/> class.
    /// </summary>
    /// <param name="category">The logger category (typically the fully-qualified class name).</param>
    /// <param name="provider">The provider that manages file output and lifecycle.</param>
    /// <param name="scopeProvider">Optional scope provider for agent context propagation.</param>
    public FileLogger(string category, FileLoggerProvider provider, IExternalScopeProvider? scopeProvider)
        : base(category, scopeProvider)
    {
        _provider = provider;
    }

    /// <inheritdoc />
    public override bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None && _provider.IsRunActive;

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

        var structured = $"{timestamp:yyyy-MM-dd HH:mm:ss.fff} [{shortLevel}] [{_category}] {cleanMessage}";
        var console = $"{timestamp:HH:mm:ss.fff} {shortLevel} [{shortCategory}] {cleanMessage}";

        if (exception is not null)
        {
            structured += Environment.NewLine + exception;
            console += Environment.NewLine + exception;
        }

        _provider.WriteMessage(structured, console);
    }
}

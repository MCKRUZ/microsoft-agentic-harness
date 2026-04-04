using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// An <see cref="ILogger"/> implementation that formats log entries and queues them
/// to a <see cref="FileLoggerProvider"/> for background file writing.
/// </summary>
/// <remarks>
/// Produces dual-format output: a structured line for <c>log.txt</c> and a
/// human-readable line for <c>console.txt</c>. Message formatting is handled
/// by <see cref="LoggingHelper"/> for consistency across all providers.
/// <para>
/// This logger does not write directly to disk — it enqueues messages to
/// the provider's bounded queue, which is drained by a background thread.
/// </para>
/// </remarks>
public sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly FileLoggerProvider _provider;
    private readonly IExternalScopeProvider? _scopeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileLogger"/> class.
    /// </summary>
    /// <param name="category">The logger category (typically the fully-qualified class name).</param>
    /// <param name="provider">The provider that manages file output and lifecycle.</param>
    /// <param name="scopeProvider">Optional scope provider for agent context propagation.</param>
    public FileLogger(string category, FileLoggerProvider provider, IExternalScopeProvider? scopeProvider)
    {
        _category = category;
        _provider = provider;
        _scopeProvider = scopeProvider;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        _scopeProvider?.Push(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None && _provider.IsRunActive;

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

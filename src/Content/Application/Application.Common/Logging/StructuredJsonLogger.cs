using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// An <see cref="ILogger"/> implementation that formats log entries as JSON objects
/// and queues them to a <see cref="StructuredJsonLoggerProvider"/> for JSONL file output.
/// </summary>
/// <remarks>
/// Each log entry becomes a single JSON object on one line (NDJSON/JSONL format),
/// including all scope properties from <see cref="ExecutionScopeProvider"/>. This enables
/// post-hoc querying of execution sessions — filtering by executor, step, operation, or level
/// without parsing human-readable log formats.
/// </remarks>
public sealed class StructuredJsonLogger : ILogger
{
    private readonly string _category;
    private readonly StructuredJsonLoggerProvider _provider;
    private readonly IExternalScopeProvider? _scopeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="StructuredJsonLogger"/> class.
    /// </summary>
    /// <param name="category">The logger category.</param>
    /// <param name="provider">The provider managing JSONL file output.</param>
    /// <param name="scopeProvider">Optional scope provider for capturing execution context.</param>
    public StructuredJsonLogger(
        string category,
        StructuredJsonLoggerProvider provider,
        IExternalScopeProvider? scopeProvider)
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
        var executionScope = ExecutionScopeProvider.GetCurrentScope(_scopeProvider);

        var entry = new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
            ["level"] = LoggingHelper.GetLevelName(logLevel),
            ["category"] = _category,
            ["eventId"] = eventId.Id,
            ["message"] = message
        };

        if (executionScope is not null)
            foreach (var (key, value) in executionScope.ToProperties())
                entry[key] = value;
        if (exception is not null)
            entry["exception"] = exception.ToString();

        _provider.WriteJsonEntry(entry);
    }
}

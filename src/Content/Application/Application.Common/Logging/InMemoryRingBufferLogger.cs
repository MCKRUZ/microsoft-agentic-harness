using Application.Common.Models;
using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// An <see cref="ILogger"/> implementation that creates <see cref="LogEntry"/> objects
/// and pushes them to an <see cref="InMemoryRingBufferLoggerProvider"/> for retention
/// in a fixed-size circular buffer.
/// </summary>
/// <remarks>
/// Entries are available via the provider's <see cref="InMemoryRingBufferLoggerProvider.GetEntries"/>
/// method for diagnostics endpoints, health checks, and debugging UIs. When the buffer
/// is full, the oldest entries are silently discarded.
/// </remarks>
public sealed class InMemoryRingBufferLogger : ILogger
{
    private readonly string _category;
    private readonly InMemoryRingBufferLoggerProvider _provider;
    private readonly IExternalScopeProvider? _scopeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryRingBufferLogger"/> class.
    /// </summary>
    /// <param name="category">The logger category.</param>
    /// <param name="provider">The provider managing the ring buffer.</param>
    /// <param name="scopeProvider">Optional scope provider for capturing agent context.</param>
    public InMemoryRingBufferLogger(
        string category,
        InMemoryRingBufferLoggerProvider provider,
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
        var entry = LogEntry.CreateFromScope(logLevel, _category, eventId, message, exception, _scopeProvider);

        _provider.AddEntry(entry);
    }
}

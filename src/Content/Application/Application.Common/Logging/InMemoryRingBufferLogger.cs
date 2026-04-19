using Domain.Common.Models;
using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// An <see cref="ILogger"/> implementation that creates <see cref="LogEntry"/> objects
/// and pushes them to an <see cref="InMemoryRingBufferLoggerProvider"/> for retention
/// in a fixed-size circular buffer.
/// </summary>
public sealed class InMemoryRingBufferLogger : BaseLogger
{
    private readonly InMemoryRingBufferLoggerProvider _provider;

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
        var entry = LogEntryFactory.CreateFromScope(logLevel, _category, eventId, message, exception, _scopeProvider);

        _provider.AddEntry(entry);
    }
}

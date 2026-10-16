using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Tests.Common;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that keeps what was written — for the few tests where a
/// decision has no other observable effect, so the log line IS the behaviour under test (for example,
/// a fallback that is otherwise indistinguishable from "nothing was configured").
/// </summary>
/// <typeparam name="T">The category type the logger is created for.</typeparam>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    /// <summary>True when an entry at <paramref name="level"/> contains <paramref name="fragment"/>.</summary>
    public bool Logged(LogLevel level, string fragment) =>
        _entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Enqueue((logLevel, formatter(state, exception)));
}

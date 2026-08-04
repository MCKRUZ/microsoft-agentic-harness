using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Presentation.AgentHub.Tests;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that keeps what was written, for the cases where the log
/// <em>is</em> the behaviour under test.
/// </summary>
/// <remarks>
/// Used sparingly and on purpose. Asserting on log text couples a test to wording, so it is worth it
/// only where a decision has no other observable effect — a turn stopped by a lost lease and a turn
/// stopped by a client disconnect end the same way on the wire, because a disconnected client has no
/// stream left to be told anything on. Which of the two happened survives only in the log, so that is
/// where the distinction has to be checked.
/// </remarks>
/// <typeparam name="T">The category type, matching the logger the subject asks for.</typeparam>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    /// <summary>Everything written so far, in order.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries.ToList();

    /// <summary>Whether any entry at <paramref name="level"/> contains <paramref name="fragment"/>.</summary>
    public bool Logged(LogLevel level, string fragment) =>
        _entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _entries.Enqueue((logLevel, formatter(state, exception)));
}

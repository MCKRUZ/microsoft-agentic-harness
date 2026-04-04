using System.Collections.Concurrent;
using Application.Common.Models;
using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// An <see cref="ILoggerProvider"/> that invokes a user-supplied callback for each
/// log entry. Designed for SDK consumers who want a simple lambda-based log handler
/// instead of implementing the full <see cref="ILoggerProvider"/> interface.
/// </summary>
/// <remarks>
/// Register via the extension method:
/// <code>
/// builder.AddCallback(entry => Console.WriteLine($"[{entry.Level}] {entry.Message}"));
/// </code>
/// The callback receives a fully-populated <see cref="LogEntry"/> with agent scope
/// data when available. Keep callbacks fast — they execute synchronously on the
/// logging thread.
/// </remarks>
/// <example>
/// <code>
/// // SDK consumer embedding the agent harness:
/// var services = new ServiceCollection();
/// services.AddLogging(builder =>
/// {
///     builder.AddCallback(entry =>
///     {
///         if (entry.Level >= LogLevel.Warning)
///             myAlertSystem.Send(entry.Message);
///     });
/// });
/// </code>
/// </example>
public sealed class CallbackLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, CallbackLogger> _loggers = new();
    private readonly Action<LogEntry> _callback;
    private readonly IExternalScopeProvider? _scopeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CallbackLoggerProvider"/> class.
    /// </summary>
    /// <param name="callback">The delegate invoked for each log entry.</param>
    /// <param name="scopeProvider">Optional scope provider for agent context extraction.</param>
    public CallbackLoggerProvider(
        Action<LogEntry> callback,
        IExternalScopeProvider? scopeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
        _scopeProvider = scopeProvider;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name =>
            new CallbackLogger(name, _callback, _scopeProvider));

    /// <inheritdoc />
    public void Dispose()
    {
        // No unmanaged resources
    }
}

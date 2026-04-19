using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// Base <see cref="ILogger"/> implementation that handles category tracking,
/// scope propagation, and log-level filtering — the boilerplate shared by
/// every custom logger in the harness.
/// </summary>
public abstract class BaseLogger : ILogger
{
    protected readonly string _category;
    protected readonly IExternalScopeProvider? _scopeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="BaseLogger"/> class.
    /// </summary>
    /// <param name="category">The logger category (typically the fully-qualified class name).</param>
    /// <param name="scopeProvider">Optional scope provider for agent context propagation.</param>
    protected BaseLogger(string category, IExternalScopeProvider? scopeProvider)
    {
        _category = category;
        _scopeProvider = scopeProvider;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        _scopeProvider?.Push(state);

    /// <inheritdoc />
    public virtual bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <inheritdoc />
    public abstract void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter);
}

using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// Wraps an <see cref="ILogger"/> so both the rendered message and any logged exception are redacted
/// before they reach the inner logger — and therefore before they reach whatever local
/// <see cref="ILoggerProvider"/> the inner logger belongs to (#457).
/// </summary>
/// <remarks>
/// <para>
/// Both halves funnel through the same <c>Log(logLevel, eventId, string, Exception?, ...)</c> overload
/// regardless of the original <c>TState</c> shape. That is deliberate, not a loss of fidelity: every
/// local <see cref="ILoggerProvider"/> in this harness (console, file, JSONL, named pipe, in-memory
/// ring buffer) only ever reads the formatted message string and the exception, never <c>TState</c>'s
/// own fields directly — confirmed by reading each one before writing this type. Re-invoking with the
/// original, untouched <c>TState</c> would let a provider's own formatter re-render the unredacted
/// original underneath this wrapper.
/// </para>
/// </remarks>
public sealed class RedactingLogger : ILogger
{
    private readonly ILogger _inner;
    private readonly ILocalLogRedactor _redactor;

    public RedactingLogger(ILogger inner, ILocalLogRedactor redactor)
    {
        _inner = inner;
        _redactor = redactor;
    }

    /// <summary>
    /// Redacts scope state the same way <see cref="Log{TState}"/> redacts a message — found in
    /// independent security review: forwarding <paramref name="state"/> unchanged meant a secret or PII
    /// value placed in scope state (a connection string, an email — categories this redactor's default
    /// set already covers) reached every local sink in cleartext, since a console/file formatter renders
    /// scope contents directly. Handles the two shapes scope state actually takes: a structured scope
    /// (<c>IEnumerable&lt;KeyValuePair&lt;string, object?&gt;&gt;</c>, from a dictionary or
    /// <c>LoggerMessage.DefineScope</c>) has its string-valued entries redacted in place; anything else
    /// is redacted via its rendered <see cref="object.ToString"/> text, mirroring how a formatter that
    /// doesn't understand the structured shape would render it anyway.
    /// </summary>
    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        if (!_redactor.Enabled)
        {
            return _inner.BeginScope(state);
        }

        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            var redactedPairs = pairs
                .Select(kv => kv.Value is string value
                    ? new KeyValuePair<string, object?>(kv.Key, _redactor.Redact(value))
                    : kv)
                .ToList();
            return _inner.BeginScope(redactedPairs);
        }

        return _inner.BeginScope(_redactor.Redact(state.ToString() ?? string.Empty));
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!_redactor.Enabled || !IsEnabled(logLevel))
        {
            _inner.Log(logLevel, eventId, state, exception, formatter);
            return;
        }

        var rendered = formatter(state, exception);
        var redactedMessage = _redactor.Redact(rendered);
        var redactedException = RedactException(exception);

        _inner.Log(logLevel, eventId, redactedMessage, redactedException, static (message, _) => message);
    }

    /// <summary>
    /// Redacts an exception's full text (not just <see cref="Exception.Message"/> — that
    /// representation drops every <see cref="Exception.InnerException"/>'s own message, so a secret
    /// nested in a wrapped exception's inner message would otherwise survive when the outer message
    /// alone is clean), returning a replacement only when something actually matched.
    /// </summary>
    private Exception? RedactException(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        var original = exception.ToString();
        var redacted = _redactor.Redact(original);
        return redacted == original ? exception : new RedactedLocalLogException(redacted);
    }
}

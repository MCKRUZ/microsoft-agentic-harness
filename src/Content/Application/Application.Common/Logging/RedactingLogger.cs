using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// Wraps an <see cref="ILogger"/> so both the rendered message and any logged exception are redacted
/// before they reach the inner logger — and therefore before they reach whatever local
/// <see cref="ILoggerProvider"/> the inner logger belongs to (#457).
/// </summary>
/// <remarks>
/// <para>
/// <c>Log</c> re-invokes the inner logger with the <em>original, untouched</em> <c>state</c> — never a
/// collapsed string — swapping in only a formatter that returns the already-redacted message text. An
/// earlier version collapsed <c>state</c> to a plain string unconditionally; CI's correctness-review
/// caught that this dropped every structured attribute (<c>{OriginalFormat}</c>, named arguments) the
/// OTel logging bridge extracts by reading <c>state</c> directly, once <c>LogsConfig.OtelExportEnabled</c>
/// is on. Preserving <c>state</c> is safe specifically because the OTel bridge is not left unprotected:
/// <c>Infrastructure.Observability.Processors.LogRecordRedactionProcessor</c> is registered ahead of its
/// exporter and independently redacts <c>LogRecord.Attributes</c>/<c>FormattedMessage</c>/<c>Exception</c>
/// on that path already — this type's own job, per #457, is the local sinks that processor never reaches
/// (console, file, JSONL, named pipe, in-memory ring buffer), and every one of those was confirmed, by
/// reading each, to only ever consume the formatted message string and the exception — never
/// <c>state</c>'s own fields directly. Whichever sink actually reads it, the formatted text it renders
/// is the redacted one.
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
    /// Redacts scope state carrying free text — found in independent security review: forwarding
    /// <paramref name="state"/> unchanged meant a secret or PII value placed in scope state (a
    /// connection string, an email) reached every local sink in cleartext, since a console/file
    /// formatter renders scope contents directly. Handles only the two shapes actually capable of
    /// carrying arbitrary text: a plain <see cref="string"/>, and a structured scope
    /// (<c>IEnumerable&lt;KeyValuePair&lt;string, object?&gt;&gt;</c>, from a dictionary or
    /// <c>LoggerMessage.DefineScope</c>) has its string-valued entries redacted in place.
    /// </summary>
    /// <remarks>
    /// Anything else passes through <em>unchanged</em> — not redacted via its rendered
    /// <see cref="object.ToString"/> text. An earlier version of this method did exactly that, and CI's
    /// correctness-review caught the regression: <c>ExecutionScope</c> (this codebase's own domain scope
    /// type, carrying executor/correlation ids and a step number — structural identifiers, never
    /// arbitrary text, so nothing here needs redacting) does not implement the structured-KVP shape
    /// above, so it fell through to the string-collapse branch. That replaced the pushed scope object
    /// with a plain <see cref="string"/>, which broke <c>ExecutionScopeProvider.GetCurrentScope</c>'s
    /// <c>scope is ExecutionScope</c> type check for every request — not merely a fidelity loss but a
    /// silent breakage of an already-wired feature, for every local sink, whenever redaction is enabled
    /// (the default). Transforming an unrecognized object risks exactly this: breaking a downstream
    /// consumer that depends on the exact runtime type of what it pushed. Only the two shapes this type
    /// can safely reconstruct after redacting are transformed; everything else is left alone.
    /// </remarks>
    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        if (!_redactor.Enabled)
        {
            return _inner.BeginScope(state);
        }

        if (state is string text)
        {
            return _inner.BeginScope(_redactor.Redact(text));
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

        return _inner.BeginScope(state);
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

        // state passes through unchanged — see this type's remarks for why that's safe. Only the
        // formatter is replaced, so whatever reads state.ToString()-equivalent text gets the redacted
        // message; whatever reads state's own fields directly gets the real ones, protected downstream
        // on the one path that matters (OTel export) by LogRecordRedactionProcessor.
        _inner.Log(logLevel, eventId, state, redactedException, (_, _) => redactedMessage);
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

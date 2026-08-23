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
    /// Redacts scope state, but only the one shape this type can safely redact without risking a
    /// downstream regression: a plain <see cref="string"/>, pushed back as the redacted string itself —
    /// no reconstruction, so nothing about it can differ from the original except the text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything else passes through <em>completely unchanged</em>. Two earlier versions of this method
    /// each tried to redact one more shape, and CI's correctness-review caught a real regression in both:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Collapsing any unrecognized object to a redacted string via <see cref="object.ToString"/> broke
    /// <c>ExecutionScopeProvider.GetCurrentScope</c>'s <c>scope is ExecutionScope</c> type check — the
    /// pushed object's runtime type must survive for that consumer to recognize it at all.
    /// </description></item>
    /// <item><description>
    /// Reconstructing a structured scope (<c>IEnumerable&lt;KeyValuePair&lt;string, object?&gt;&gt;</c>)
    /// as a plain <c>List&lt;KeyValuePair&lt;string, object?&gt;&gt;</c> broke every local formatter that
    /// renders a scope by calling <see cref="object.ToString"/> on whatever was pushed —
    /// <c>ColorfulConsoleFormatter.WriteScopeInformation</c> does exactly this — because a
    /// compiler-generated templated <c>BeginScope("... {Arg} ...", args)</c> call's state implements that
    /// same interface <em>and</em> overrides <c>ToString()</c> to render the interpolated text; the
    /// reconstructed <c>List&lt;T&gt;</c> has no such override, so the rendered scope silently became
    /// useless boilerplate instead of the original text, for every templated scope, everywhere.
    /// </description></item>
    /// </list>
    /// <para>
    /// A templated <c>BeginScope</c> call is also the one realistic way a secret reaches scope state in
    /// this codebase — and it is exactly the shape the fix above cannot safely rewrite without breaking
    /// its own rendering identity. That gap is real and tracked (issue #500) rather than "fixed" a third
    /// time with the same category of regression; the plain-string case this method does handle is the
    /// one already-common, already-safe pattern (<c>BeginScope("free text here")</c>) with zero
    /// type-identity risk.
    /// </para>
    /// </remarks>
    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        if (_redactor.Enabled && state is string text)
        {
            return _inner.BeginScope(_redactor.Redact(text));
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

namespace Application.Common.Logging;

/// <summary>
/// Replaces an exception <see cref="RedactingLogger"/> caught something to redact in. Carries the
/// already-redacted full text (type, message, stack frames, and the whole inner-exception chain) so
/// whatever a local sink's formatter does with the exception — <c>.Message</c>, <c>.ToString()</c>, or
/// both — reads the redacted text rather than the original.
/// </summary>
/// <remarks>
/// Unlike the OTel bridge's <c>LogRecordRedactionProcessor</c> (which keeps <c>Exception.Message</c>
/// short and stashes the full redacted text in a separate structured attribute, because the OTLP
/// exporter treats <c>exception.message</c> as a standardized, dashboard-grouped field), a local sink
/// has no such contract to protect — console and file output print the whole exception as one blob
/// regardless — so <see cref="ToString"/> returning the complete redacted text directly is the simpler,
/// equally-safe choice here.
/// </remarks>
public sealed class RedactedLocalLogException : Exception
{
    private readonly string _redactedText;

    /// <param name="redactedText">The exception's full text, already redacted.</param>
    public RedactedLocalLogException(string redactedText) : base(FirstLine(redactedText))
    {
        _redactedText = redactedText;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns the complete redacted text rather than the base <see cref="Exception.ToString"/>
    /// formatting (which would prefix this type's own name and append its own, empty stack trace) —
    /// a formatter calling <c>ToString()</c> on this exception should see exactly the redacted
    /// original, not a wrapper's framing around it.
    /// </remarks>
    public override string ToString() => _redactedText;

    private static string FirstLine(string text)
    {
        var newlineIndex = text.IndexOf('\n');
        return newlineIndex >= 0 ? text[..newlineIndex] : text;
    }
}

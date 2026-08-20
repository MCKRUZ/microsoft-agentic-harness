namespace Infrastructure.Observability.Processors;

/// <summary>
/// Replaces an exception <see cref="LogRecordRedactionProcessor"/> redacted before it reaches the
/// OTLP exporter, in place of the original type.
/// </summary>
/// <remarks>
/// The OTLP log serializer derives the exported <c>exception.type</c> attribute from
/// <c>LogRecord.Exception.GetType()</c> — there is no way to keep the original exception's real
/// type there while replacing its message, short of dynamically reconstructing an instance of that
/// exact type (fragile: most exception types have no guaranteed <c>(string)</c> constructor, and
/// constructing one can have side effects). A generic <see cref="System.Exception"/> would report
/// <c>exception.type = "System.Exception"</c>, indistinguishable from a genuine bare
/// <c>throw new Exception(...)</c> elsewhere. This type exists only so the export self-announces
/// that redaction happened — a responder sees <c>exception.type = "RedactedLogException"</c> and
/// knows the original type name is embedded in the (redacted) message text instead of the type
/// attribute, rather than silently losing that signal with no indication why.
/// </remarks>
/// <param name="message">
/// A short, fixed summary — the original type name plus a pointer to where the full redacted detail
/// lives. Deliberately not the full redacted text: the OTLP exporter reads this directly into the
/// standardized <c>exception.message</c>/<c>exception.stacktrace</c> fields, which callers expect to
/// stay short and stable for grouping and alerting — see <c>LogRecordRedactionProcessor.RedactException</c>.
/// </param>
public sealed class RedactedLogException(string message) : Exception(message);

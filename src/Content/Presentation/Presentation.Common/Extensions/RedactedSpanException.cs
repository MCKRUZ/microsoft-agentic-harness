namespace Presentation.Common.Extensions;

/// <summary>
/// Replaces an exception before <see cref="System.Diagnostics.Activity.AddException"/> turns it into
/// the span's "exception" event, in place of the original type. The span-side counterpart of
/// <c>Infrastructure.Observability.Processors.RedactedLogException</c> — see that type's remarks for
/// why a generic <see cref="System.Exception"/> would not do: the exported <c>exception.type</c>
/// attribute is derived from <see cref="object.GetType"/>, so a bare <see cref="System.Exception"/>
/// would report <c>exception.type = "System.Exception"</c>, indistinguishable from a genuine bare
/// <c>throw new Exception(...)</c> elsewhere. This type's name is the signal that redaction happened.
/// </summary>
/// <param name="message">
/// The already-redacted exception message. <see cref="System.Diagnostics.Activity.AddException"/>
/// uses this verbatim for the <c>exception.message</c> tag and this instance's
/// <see cref="System.Exception.ToString"/> (which, with no inner exception, is just the type name
/// plus this message) for <c>exception.stacktrace</c> — both already redacted by construction, so
/// there is nothing left for the framework's own auto-population to fill in unredacted.
/// </param>
public sealed class RedactedSpanException(string message) : Exception(message);

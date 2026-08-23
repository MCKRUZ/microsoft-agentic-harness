namespace Application.Common.Logging;

/// <summary>
/// Redacts free text before it reaches a local <see cref="Microsoft.Extensions.Logging.ILoggerProvider"/>
/// sink — console, file, JSONL, named pipe, or any future provider (#457).
/// </summary>
/// <remarks>
/// <para>
/// Owned here, in <c>Application.Common</c>, rather than alongside the concrete content redactor: this
/// project has no reference to <c>Application.AI.Common</c> (where <c>IContentRedactionFilter</c> and
/// <c>RedactionCategory</c> live) and adding one would invert the layering the two "Common" projects are
/// split to preserve — <c>Application.Common</c> stays AI-agnostic; AI-specific concerns live one layer
/// up. This interface is deliberately the smallest shape a redactor needs to be useful here: no
/// categories, no tool-name context, just text in and text out. The richer decision (which categories,
/// which config) is entirely the implementing adapter's business.
/// </para>
/// <para>
/// <strong>Optional by design.</strong> <see cref="Extensions.IServiceCollectionExtensions.ConfigureLogging"/>
/// resolves this via <c>IServiceProvider.GetService</c>, not <c>GetRequiredService</c> — a host that
/// never registers an implementation gets a passthrough logging pipeline, identical to before this type
/// existed, rather than a startup failure. The one real implementation lives in
/// <c>Infrastructure.Observability</c>, wrapping the same <c>IContentRedactionFilter</c> and
/// <c>LogsConfig</c> the OTel logging bridge's own redaction already uses — one config knob, both
/// surfaces.
/// </para>
/// </remarks>
public interface ILocalLogRedactor
{
    /// <summary>
    /// Whether redaction is currently active. Checked once per log call so a disabled redactor costs
    /// a property read, not a full scrub with nothing to show for it.
    /// </summary>
    bool Enabled { get; }

    /// <summary>Redacts known secret/PII patterns from <paramref name="text"/>.</summary>
    /// <param name="text">The raw text — a formatted log message, or an exception's full text.</param>
    /// <returns>The redacted text. Identical to <paramref name="text"/> when nothing matched.</returns>
    string Redact(string text);
}

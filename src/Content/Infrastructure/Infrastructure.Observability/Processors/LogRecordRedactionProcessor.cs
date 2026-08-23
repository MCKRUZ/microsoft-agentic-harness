using System.Collections.Immutable;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Redaction;
using Domain.Common.Config.Observability;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Infrastructure.Observability.Processors;

/// <summary>
/// Scrubs PII / secret content from OpenTelemetry <see cref="LogRecord"/>s before
/// they reach any exporter. The log-signal sibling of the span-side
/// <see cref="PiiFilteringProcessor"/> — both reuse the harness's content redactor, though the two
/// scrub at different points for the same reason: <see cref="PiiFilteringProcessor"/> only
/// deletes/hashes span tags by exact key and cannot pattern-scan a span's exception event after the
/// fact — <c>ActivityEvent</c> is immutable once added — so the equivalent free-text scrub for
/// exception content on spans happens earlier, in
/// <c>Presentation.Common.Extensions.OpenTelemetryServiceCollectionExtensions.BuildRedactingExceptionEnricher</c>,
/// rather than in a processor here.
/// </summary>
/// <remarks>
/// <para>
/// Registered <strong>first</strong> in the logger pipeline — ahead of the OTLP
/// (or any other) exporter — so redaction runs in-process before a record is
/// serialized or copied for batching. Processor <see cref="BaseProcessor{T}.OnEnd"/>
/// callbacks run in registration order on the emitting thread, and the batch
/// exporter snapshots the pooled <see cref="LogRecord"/>; a redactor registered
/// after the exporter would therefore export the raw record.
/// <strong>Scope: the OTel logging bridge only.</strong> This processor governs
/// what an OTLP (or other OTel) exporter sends off-box — nothing more. A
/// standard <see cref="Microsoft.Extensions.Logging.ILoggerProvider"/> registered
/// alongside the OTel bridge (e.g. the console provider, wired unconditionally
/// wherever this host also adds one) receives the same <c>ILogger</c> calls
/// through its own, entirely separate path and is never touched by this
/// processor — it still writes the raw, unredacted exception. Because the
/// scrub happens before the OTLP export specifically, PII never reaches that
/// wire even when a downstream collector — not the app — forwards the logs to
/// Event Hub / a SIEM; it says nothing about any other sink this host emits to.
/// </para>
/// <para>
/// Four surfaces are scrubbed: the rendered <see cref="LogRecord.FormattedMessage"/>
/// (populated because the pipeline sets <c>IncludeFormattedMessage = true</c>),
/// the <see cref="LogRecord.Body"/>, every string-valued entry in
/// <see cref="LogRecord.Attributes"/> (the promoted structured fields), and
/// <see cref="LogRecord.Exception"/> (see <see cref="RedactException"/> — the OTLP
/// exporter serializes it independently of the other three, into its own
/// standardized fields). The underlying <see cref="IContentRedactionFilter"/> is
/// intentionally over-redactive: a false positive that masks a token is
/// acceptable, a false negative that leaks a credit-card number is not.
/// </para>
/// </remarks>
public sealed class LogRecordRedactionProcessor : BaseProcessor<LogRecord>
{
    private readonly IContentRedactionFilter _filter;
    private readonly ImmutableArray<RedactionCategory> _categories;
    private readonly bool _enabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogRecordRedactionProcessor"/> class.
    /// </summary>
    /// <param name="filter">The content redactor reused across all signals.</param>
    /// <param name="config">The logs-signal configuration (redaction toggle + categories).</param>
    /// <param name="logger">Logger for surfacing unrecognised category names once at startup.</param>
    public LogRecordRedactionProcessor(
        IContentRedactionFilter filter,
        LogsConfig config,
        ILogger<LogRecordRedactionProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _filter = filter;
        _enabled = config.RedactionEnabled;
        // Fail-safe-not-open fallback (empty-but-enabled → every category) lives once in
        // RedactionCategoryParser, shared with the local-sink redactor (#457).
        _categories = RedactionCategoryParser.Parse(config.RedactionCategories, logger, _enabled);

        logger.LogInformation(
            "Log-record redaction initialized: enabled={Enabled}, {CategoryCount} categories active.",
            _enabled,
            _categories.Length);
    }

    /// <inheritdoc />
    public override void OnEnd(LogRecord data)
    {
        if (!_enabled || _categories.Length == 0 || data is null)
        {
            return;
        }

        if (data.FormattedMessage is { Length: > 0 } message)
        {
            data.FormattedMessage = _filter.Redact(message, _categories);
        }

        if (data.Body is { Length: > 0 } body)
        {
            data.Body = _filter.Redact(body, _categories);
        }

        RedactAttributes(data);
        RedactException(data);
    }

    /// <summary>
    /// The attribute key the full redacted exception text is stored under — see
    /// <see cref="RedactException"/> for why it isn't just <see cref="Exception.Message"/>.
    /// </summary>
    private const string RedactedExceptionDetailAttributeKey = "exception.redacted_details";

    /// <summary>
    /// Scrubs <see cref="LogRecord.Exception"/>, the one surface the three scrubs above never
    /// touch — the OTLP exporter serializes it independently into <c>exception.message</c> /
    /// <c>exception.stacktrace</c>, so an exception whose message embeds a secret (a connection
    /// string, a credential-bearing URI) reaches the exporter unredacted even with the other three
    /// scrubs in place.
    /// </summary>
    /// <remarks>
    /// Checks the exception's full <c>ToString()</c> text, not just <see cref="Exception.Message"/> —
    /// that representation recursively includes every <see cref="Exception.InnerException"/>'s own
    /// message via its <c>" ---> "</c> chain, so a secret nested in a wrapped exception's inner
    /// message is still caught even when the outer message itself is clean (e.g. a generic "dispatch
    /// failed" wrapping a lower-level exception whose message carries a connection string). This is
    /// the same text the OTLP exporter itself exports as <c>exception.stacktrace</c> — confirmed
    /// against its serializer source, which calls the SDK's own internal, culture-invariant
    /// <c>ToInvariantString()</c> on whatever <see cref="LogRecord.Exception"/> ends up being (that
    /// internal helper isn't visible to this assembly, so this method calls the ordinary,
    /// culture-sensitive <c>ToString()</c> instead — a difference confined to stack-frame boilerplate
    /// like the localized "at"/"bei" prefix, never to a secret: an exception's own
    /// <see cref="Exception.Message"/> text is a literal .NET string, not culture-translated, so what
    /// the redaction filter scans for a match is identical either way).
    /// <para>
    /// <strong>Where the redacted text goes, and why not into <see cref="Exception.Message"/>.</strong>
    /// Confirmed against the OTLP exporter's serializer: it sets <c>exception.message</c> directly
    /// from <see cref="Exception.Message"/> and <c>exception.stacktrace</c> from
    /// <c>ToInvariantString()</c> — both standardized, short-content fields dashboards group and
    /// alert on. Putting the full redacted <c>ToString()</c> dump (original message, stack frames,
    /// and the whole inner-exception chain, all flattened into one string) into
    /// <see cref="Exception.Message"/> would blow both fields up into a multi-line, effectively
    /// unique-per-call blob, breaking that grouping for every redacted log line — the opposite of
    /// what a "short message" field is for. So the replacement's own <see cref="Exception.Message"/>
    /// stays a short, fixed summary (type name plus a pointer to where the detail lives), and the
    /// full redacted text goes into a new <see cref="LogRecord.Attributes"/> entry under
    /// <see cref="RedactedExceptionDetailAttributeKey"/> instead — a normal structured field, not a
    /// semantic-convention one anything expects to stay short.
    /// </para>
    /// <see cref="Exception.Message"/> has no public setter, so an in-place edit isn't possible
    /// regardless; a replacement instance is built instead, with no
    /// <see cref="Exception.InnerException"/> of its own, so nothing unredacted can still be reached
    /// through it. The replacement is a <see cref="RedactedLogException"/>, not a bare
    /// <see cref="Exception"/> — see its own remarks for why the exported <c>exception.type</c>
    /// attribute needs to say "this was redacted" rather than silently reporting a generic type that
    /// looks identical to an unrelated bare throw elsewhere. Only mutates the record when the filter
    /// actually matched something, matching the no-op-when-nothing-matched contract every other
    /// redaction call here honors.
    /// </remarks>
    private void RedactException(LogRecord data)
    {
        if (data.Exception is not { } exception)
        {
            return;
        }

        var original = exception.ToString();
        var redacted = _filter.Redact(original, _categories);
        if (redacted == original)
        {
            return;
        }

        var typeName = exception.GetType().Name;
        data.Exception = new RedactedLogException(
            $"{typeName} (redacted — see '{RedactedExceptionDetailAttributeKey}' attribute for detail)");

        var attributes = data.Attributes is { } existing
            ? new List<KeyValuePair<string, object?>>(existing)
            : [];
        attributes.Add(new KeyValuePair<string, object?>(RedactedExceptionDetailAttributeKey, redacted));
        data.Attributes = attributes;
    }

    /// <summary>
    /// Rewrites string-valued attributes in place, allocating a replacement list only
    /// when at least one value actually changed (the common case is no PII → no alloc).
    /// </summary>
    private void RedactAttributes(LogRecord data)
    {
        var attributes = data.Attributes;
        if (attributes is null || attributes.Count == 0)
        {
            return;
        }

        List<KeyValuePair<string, object?>>? rewritten = null;
        for (var i = 0; i < attributes.Count; i++)
        {
            var attribute = attributes[i];
            if (attribute.Value is string raw && raw.Length > 0)
            {
                var scrubbed = _filter.Redact(raw, _categories);
                if (scrubbed != raw)
                {
                    rewritten ??= [.. attributes];
                    rewritten[i] = new KeyValuePair<string, object?>(attribute.Key, scrubbed);
                }
            }
        }

        if (rewritten is not null)
        {
            data.Attributes = rewritten;
        }
    }

}

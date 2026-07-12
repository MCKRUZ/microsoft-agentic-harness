using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Redaction;
using Domain.Common.Config.Observability;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Infrastructure.Observability.Processors;

/// <summary>
/// Scrubs PII / secret content from OpenTelemetry <see cref="LogRecord"/>s before
/// they reach any exporter. The log-signal counterpart to the span-side
/// <see cref="PiiFilteringProcessor"/>: one PII processor per signal, both reusing
/// the harness's content redactor.
/// </summary>
/// <remarks>
/// <para>
/// Registered <strong>first</strong> in the logger pipeline — ahead of the OTLP
/// (or any other) exporter — so redaction runs in-process before a record is
/// serialized or copied for batching. Processor <see cref="BaseProcessor{T}.OnEnd"/>
/// callbacks run in registration order on the emitting thread, and the batch
/// exporter snapshots the pooled <see cref="LogRecord"/>; a redactor registered
/// after the exporter would therefore export the raw record. Because the scrub
/// happens before export, PII never transits the wire even when a downstream
/// collector — not the app — forwards the logs to Event Hub / a SIEM.
/// </para>
/// <para>
/// Three surfaces are scrubbed: the rendered <see cref="LogRecord.FormattedMessage"/>
/// (populated because the pipeline sets <c>IncludeFormattedMessage = true</c>),
/// the <see cref="LogRecord.Body"/>, and every string-valued entry in
/// <see cref="LogRecord.Attributes"/> (the promoted structured fields). The
/// underlying <see cref="IContentRedactionFilter"/> is intentionally
/// over-redactive: a false positive that masks a token is acceptable, a false
/// negative that leaks a credit-card number is not.
/// </para>
/// </remarks>
public sealed class LogRecordRedactionProcessor : BaseProcessor<LogRecord>
{
    private readonly IContentRedactionFilter _filter;
    private readonly RedactionCategory[] _categories;
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
        _categories = ParseCategories(config.RedactionCategories, logger);

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

    /// <summary>
    /// Parses the configured category names to <see cref="RedactionCategory"/> values,
    /// skipping (and logging) any name the enum does not recognise. Startup validation
    /// (<c>LogsConfigValidator</c>) already rejects unknown names, so this is defence in
    /// depth for hosts that bypass the validated-options pipeline.
    /// </summary>
    private static RedactionCategory[] ParseCategories(
        IReadOnlyList<string>? names,
        ILogger logger)
    {
        if (names is null || names.Count == 0)
        {
            return [];
        }

        var parsed = new List<RedactionCategory>(names.Count);
        foreach (var name in names)
        {
            if (Enum.TryParse<RedactionCategory>(name?.Trim(), ignoreCase: true, out var category))
            {
                parsed.Add(category);
            }
            else
            {
                logger.LogWarning(
                    "Ignoring unknown log-redaction category '{Category}'. Valid values: {Valid}.",
                    name,
                    string.Join(", ", Enum.GetNames<RedactionCategory>()));
            }
        }

        return [.. parsed.Distinct()];
    }
}

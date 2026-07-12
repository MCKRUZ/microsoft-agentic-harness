namespace Domain.Common.Config.Observability;

/// <summary>
/// Configuration for exporting application logs as an OpenTelemetry signal.
/// OFF by default — the harness has always exported <em>traces</em> and
/// <em>metrics</em> via OTel, but logs ran on a separate pipe (console / file /
/// JSONL / ring-buffer) that never reached the collector. Turning
/// <see cref="OtelExportEnabled"/> on bridges <c>ILogger</c> records into the
/// OTel logs pipeline so they land in the same backend (e.g. Grafana) next to
/// the trace waterfall and metric charts.
/// </summary>
/// <remarks>
/// <para>
/// The local <c>ILogger</c> sinks are unaffected: this signal is
/// <strong>additional</strong>, not a replacement. When enabled, PII is scrubbed
/// in-process (see <see cref="RedactionEnabled"/>) before any log record leaves
/// the process, so nothing sensitive transits the OTLP wire — even when the
/// collector, not the app, ultimately forwards the logs.
/// </para>
/// <para>
/// Binds from <c>AppConfig:Observability:Logs</c>. Validated at host start by
/// <c>LogsConfigValidator</c> (<c>ValidateOnStart</c>), so a malformed level or
/// redaction category fails the boot instead of silently degrading.
/// </para>
/// </remarks>
public sealed class LogsConfig
{
    /// <summary>
    /// Master switch for the <c>ILogger</c> → OpenTelemetry logs bridge. When
    /// <c>false</c> (default), no OTel logs pipeline is wired and logging behaves
    /// exactly as before (local sinks only). Default: <c>false</c>.
    /// </summary>
    public bool OtelExportEnabled { get; set; }

    /// <summary>
    /// Minimum severity a log record must have to be <em>exported</em> via OTel,
    /// independent of the local sinks' own levels. Parses to a
    /// <c>Microsoft.Extensions.Logging.LogLevel</c> (<c>Trace</c>, <c>Debug</c>,
    /// <c>Information</c>, <c>Warning</c>, <c>Error</c>, <c>Critical</c>,
    /// <c>None</c>). Default: <c>Information</c> — export the full routine +
    /// problem trail. Set <c>Warning</c> to cap Event Hub / backend volume and
    /// cost. Applied as a provider-scoped filter on the OTel logger provider.
    /// </summary>
    public string MinExportLevel { get; set; } = "Information";

    /// <summary>
    /// Whether PII / secret content is scrubbed from log records before export.
    /// Default: <c>true</c> — a compliance-sensitive template must not leak PII
    /// onto the wire, so redaction is on whenever export is on. The scrub runs
    /// as the first pipeline stage (before any exporter), covering the formatted
    /// message, the body, and every string-valued attribute.
    /// </summary>
    public bool RedactionEnabled { get; set; } = true;

    /// <summary>
    /// Names of <c>Domain.AI.Telemetry.Redaction.RedactionCategory</c> values the
    /// redactor applies before export. Unknown names fail validation at boot.
    /// Default: the full set, so a consumer that flips
    /// <see cref="OtelExportEnabled"/> on without tuning categories still gets the
    /// safest (over-redactive) posture.
    /// </summary>
    /// <remarks>
    /// Held as strings (not the enum) because the <c>AppConfig</c> hierarchy lives
    /// in <c>Domain.Common</c>, which must not depend on <c>Domain.AI</c>. The
    /// Infrastructure <c>LogRecordRedactionProcessor</c> parses these back to the
    /// enum and logs any value it does not recognise. Mirrors
    /// <c>ContentCaptureConfig.RedactionCategories</c>.
    /// </remarks>
    public List<string> RedactionCategories { get; set; } =
    [
        "Email",
        "Phone",
        "Ssn",
        "CreditCard",
        "IpAddress",
        "AwsKey",
        "JwtToken",
        "Generic",
    ];
}

using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for records the hash-chained audit trails could not append when they were made, and that were
/// held for replay (#995, #1001).
/// </summary>
public static class AuditMetrics
{
    /// <summary>
    /// Audit records that could not be appended to their trail when they were made (a busy chain, a full disk, a folder
    /// that cannot be written). Tags: <see cref="AuditConventions.Chain"/> and <see cref="AuditConventions.Outcome"/>:
    /// <c>pending</c> when the record is held for replay, <c>lost</c> when it is not kept anywhere durable, <c>stuck</c>
    /// (once per file in each process) when a held file cannot be opened, so replay cannot write it. Operators alert on
    /// <c>lost</c> and <c>stuck</c>.
    /// </summary>
    public static Counter<long> WriteFailures { get; } =
        AppInstrument.Meter.CreateCounter<long>(
            AuditConventions.WriteFailures, "{failure}", "Audit records that could not be appended to their trail when made");

    /// <summary>
    /// Records held pending that replay has since written to their trail, tagged <see cref="AuditConventions.Chain"/>.
    /// Pending failures minus replayed records is what is still waiting.
    /// </summary>
    public static Counter<long> RecordsReplayed { get; } =
        AppInstrument.Meter.CreateCounter<long>(
            AuditConventions.RecordsReplayed, "{record}", "Held audit records later written to their trail");
}

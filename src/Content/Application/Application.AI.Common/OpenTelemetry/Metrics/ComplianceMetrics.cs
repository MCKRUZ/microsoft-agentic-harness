using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for the compliance audit trail (#995), the durable proof of every erasure, data export and
/// document removal.
/// </summary>
public static class ComplianceMetrics
{
    /// <summary>
    /// Compliance records that could not be appended to the trail when they were made (a busy chain, a full disk, a
    /// folder that cannot be written). The request they record is never refused for it, so this counter is the signal
    /// operators alert on. Tags: <see cref="ComplianceConventions.Action"/> and <see cref="ComplianceConventions.Outcome"/>:
    /// <c>pending</c> when the record is held for replay, <c>lost</c> when it is not kept anywhere durable, <c>stuck</c>
    /// (once per file in each process) when a held file cannot be opened, so replay cannot write it.
    /// </summary>
    public static Counter<long> AuditWriteFailures { get; } =
        AppInstrument.Meter.CreateCounter<long>(
            ComplianceConventions.AuditWriteFailures, "{failure}", "Compliance audit records that could not be appended when made");

    /// <summary>
    /// Records held pending that replay has since written to the trail. Pending failures minus replayed records is what
    /// is still waiting.
    /// </summary>
    public static Counter<long> AuditRecordsReplayed { get; } =
        AppInstrument.Meter.CreateCounter<long>(
            ComplianceConventions.AuditRecordsReplayed, "{record}", "Held compliance audit records later written to the trail");
}

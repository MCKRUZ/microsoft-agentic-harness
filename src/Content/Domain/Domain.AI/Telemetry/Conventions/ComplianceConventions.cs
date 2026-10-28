namespace Domain.AI.Telemetry.Conventions;

/// <summary>
/// Telemetry names for the compliance audit trail (#969, #995): the record of every erasure, data export and document
/// removal.
/// </summary>
public static class ComplianceConventions
{
    /// <summary>Compliance records that could not be appended to the trail when they were made.</summary>
    public const string AuditWriteFailures = "agent.compliance.audit_write_failures";

    /// <summary>Compliance records held pending that were later written to the trail.</summary>
    public const string AuditRecordsReplayed = "agent.compliance.audit_records_replayed";

    /// <summary>
    /// The request a record belongs to: <c>right_to_erasure</c>, <c>subject_access_export</c> or
    /// <c>document_removal</c>, or a request a deployment adds of its own. Always set from code, never a correlation id:
    /// on a replayed record, read back from a held file, anything but the three shipped requests is <c>unknown</c>.
    /// </summary>
    public const string Action = "agent.compliance.action";

    /// <summary>What became of a record that could not be appended; one of <see cref="OutcomeValues"/>.</summary>
    public const string Outcome = "agent.compliance.audit.outcome";

    /// <summary>The values of <see cref="Outcome"/>.</summary>
    public static class OutcomeValues
    {
        /// <summary>Held in the pending folder, to be written to the trail by replay.</summary>
        public const string Pending = "pending";

        /// <summary>
        /// Not kept anywhere durable: the pending folder could not be written either, or a held file could not be read
        /// back as a record. The record survives only in the error log line. Alert on this.
        /// </summary>
        public const string Lost = "lost";

        /// <summary>
        /// Held, but the held file cannot be opened (another account owns it, another process keeps it open), so replay
        /// cannot write it. Counted once per file in each process, not on every pass, so the count is how many files are
        /// stuck rather than how long. Alert on this as on <see cref="Lost"/>: the record is not in the trail.
        /// </summary>
        public const string Stuck = "stuck";
    }
}

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
    /// <c>document_removal</c>. A fixed set, never a correlation id.
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
    }
}

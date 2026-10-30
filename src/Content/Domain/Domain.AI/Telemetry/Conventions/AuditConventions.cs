namespace Domain.AI.Telemetry.Conventions;

/// <summary>
/// Telemetry names for the six hash-chained audit trails (governance, escalations, changes, egress, drift and compliance)
/// when a record cannot be appended and is held for replay (#995, #1001).
/// </summary>
public static class AuditConventions
{
    /// <summary>Audit records that could not be appended to their trail when they were made.</summary>
    public const string WriteFailures = "agent.audit.write_failures";

    /// <summary>Audit records held pending that were later written to their trail.</summary>
    public const string RecordsReplayed = "agent.audit.records_replayed";

    /// <summary>Audit records still held for replay after the last pass, per trail.</summary>
    public const string RecordsHeld = "agent.audit.records_held";

    /// <summary>
    /// The trail a record belongs to: <c>governance</c>, <c>escalations</c>, <c>changes</c>, <c>egress</c>, <c>drift</c> or
    /// <c>compliance</c>. Always set from code, never from a record's content, so it cannot add a time series.
    /// </summary>
    public const string Chain = "agent.audit.chain";

    /// <summary>What became of a record that could not be appended; one of <see cref="OutcomeValues"/>.</summary>
    public const string Outcome = "agent.audit.outcome";

    /// <summary>The values of <see cref="Outcome"/>.</summary>
    public static class OutcomeValues
    {
        /// <summary>Held in the trail's pending folder, to be written to the trail by replay.</summary>
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

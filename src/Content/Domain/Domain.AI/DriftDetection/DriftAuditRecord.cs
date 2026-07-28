namespace Domain.AI.DriftDetection;

/// <summary>
/// Discriminator for <see cref="DriftAuditRecord"/> entries.
/// Determines how the <see cref="DriftAuditRecord.Data"/> field should be interpreted.
/// </summary>
public enum DriftAuditRecordType
{
    /// <summary>A drift event was detected.</summary>
    Detected,
    /// <summary>A drift event was resolved.</summary>
    Resolved,
    /// <summary>A baseline was updated (recalculated or adjusted).</summary>
    BaselineUpdated,
    /// <summary>An escalation was triggered from a drift event.</summary>
    EscalationTriggered,
    /// <summary>
    /// An evaluation was pushed through the drift HTTP surface. The payload records the
    /// authenticated caller's identity — evaluation pushes move EWMA state and feed future
    /// baselines, so every push must be attributable to a principal.
    /// </summary>
    EvaluationPushed,
    /// <summary>
    /// A baseline recalculation was requested through the drift HTTP surface. The payload
    /// records the authenticated caller's identity and the request outcome; the recalculated
    /// baseline itself is still audited by the service as <see cref="BaselineUpdated"/>.
    /// </summary>
    BaselineRecalculationRequested
}

/// <summary>
/// A single audit log entry for a drift detection lifecycle event.
/// Used by <c>IDriftAuditStore</c> for append-only JSONL persistence.
/// The <see cref="Data"/> field contains the serialized event data,
/// discriminated by <see cref="RecordType"/>.
/// </summary>
public sealed record DriftAuditRecord
{
    /// <summary>Unique identifier for this audit record.</summary>
    public required Guid RecordId { get; init; }

    /// <summary>Correlates to the originating drift event.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Discriminator for deserialization of <see cref="Data"/>.</summary>
    public required DriftAuditRecordType RecordType { get; init; }

    /// <summary>
    /// Serialized JSON payload containing event-specific data.
    /// Deserialization target depends on <see cref="RecordType"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><see cref="DriftAuditRecordType.Detected"/> → serialized <see cref="DriftEvent"/></item>
    ///   <item><see cref="DriftAuditRecordType.Resolved"/> → serialized <see cref="DriftResolution"/></item>
    ///   <item><see cref="DriftAuditRecordType.BaselineUpdated"/> → serialized <see cref="DriftBaseline"/></item>
    ///   <item><see cref="DriftAuditRecordType.EscalationTriggered"/> → serialized escalation reference</item>
    ///   <item><see cref="DriftAuditRecordType.EvaluationPushed"/> → serialized operator-action envelope (caller identity, scope, outcome)</item>
    ///   <item><see cref="DriftAuditRecordType.BaselineRecalculationRequested"/> → serialized operator-action envelope (caller identity, scope, outcome)</item>
    /// </list>
    /// </remarks>
    public required string Payload { get; init; }

    /// <summary>When this audit record was created.</summary>
    public required DateTimeOffset RecordedAt { get; init; }
}

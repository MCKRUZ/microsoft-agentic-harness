namespace Infrastructure.AI.Audit;

/// <summary>One hash-chained audit log's files, the lock every writer of it takes (#416), and where its held records wait (#1001).</summary>
/// <param name="Name">The log's name in diagnostics and metrics.</param>
/// <param name="Segments">Its files: one per day, after the earlier single file if there is one.</param>
/// <param name="LockPath">The file every process writing the log locks while appending.</param>
/// <param name="PendingPath">
/// The folder a record that could not be appended is held in until replay writes it (<see cref="PendingAuditRecords"/>):
/// a sibling of the day-file folder, so it is never read as part of the chain or archived.
/// </param>
/// <param name="RecordProperty">
/// A property every record of this log carries, as written (snake_case). A held file without it is no record of this log and
/// is set aside rather than replayed into the chain.
/// </param>
public sealed record AuditChainLocation(
    string Name, DatedAuditSegments Segments, string LockPath, string PendingPath, string RecordProperty);

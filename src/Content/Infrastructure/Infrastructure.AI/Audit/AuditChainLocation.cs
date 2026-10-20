namespace Infrastructure.AI.Audit;

/// <summary>One hash-chained audit log's files and the lock every writer of it takes (#416).</summary>
/// <param name="Name">The log's name in diagnostics.</param>
/// <param name="Segments">Its files: one per day, after the earlier single file if there is one.</param>
/// <param name="LockPath">The file every process writing the log locks while appending.</param>
public sealed record AuditChainLocation(string Name, DatedAuditSegments Segments, string LockPath);

namespace Domain.AI.Audit;

/// <summary>
/// A point a hash-chained audit log was last verified up to: the sequence number of its newest record
/// at the time, and that record's hash (#416).
/// </summary>
/// <remarks>
/// <para>
/// A hash chain proves that no record was edited, removed or reordered <em>relative to the records
/// around it</em>. It cannot, on its own, notice that the newest records were cut off, or that the
/// whole file was deleted and a fresh chain grown in its place: what is left is still internally
/// consistent. A checkpoint closes that. Each verification pass records one, and the next pass
/// requires the chain to still reach the checkpoint's sequence with the same hash there; a chain that
/// is shorter, or whose record at that sequence differs, is reported as broken.
/// </para>
/// <para>
/// Only as trustworthy as where it is kept. The verifier keeps it beside its receipts and also emits
/// the head in its structured log, which leaves the host when log export is on; a writer with full
/// control of the host can rewrite the local copy but not the exported one.
/// </para>
/// </remarks>
/// <param name="Sequence">The sequence number of the newest record when the checkpoint was taken.</param>
/// <param name="Hash">That record's hash.</param>
public sealed record AuditChainCheckpoint(long Sequence, string Hash);

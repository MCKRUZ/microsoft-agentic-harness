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
/// <strong>What it covers.</strong> Everything up to the last clean pass. Records appended after it (by
/// default up to a day's worth, at the default verification interval) are protected by the hash links
/// against edits in the middle, but not against being cut off: only the next pass's checkpoint covers
/// them.
/// </para>
/// <para>
/// <strong>Where it is kept, and the limit that follows.</strong> Beside the verification receipts, on
/// the same host. Whoever can write both the log and that folder (by default the same service account)
/// can remove the checkpoint; the next pass after a restart then re-baselines, with a warning rather
/// than an alarm. An anchor kept off the host would close that, and is not provided here.
/// </para>
/// </remarks>
/// <param name="Sequence">The sequence number of the newest record when the checkpoint was taken.</param>
/// <param name="Hash">That record's hash.</param>
public sealed record AuditChainCheckpoint(long Sequence, string Hash);

namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// The tamper-evident seal persisted alongside a durable governance record (a resolved
/// escalation outcome or a change proposal): the signature, the identifier of the key that
/// produced it (so a rotated key set can still verify historical rows), and the hash fields
/// the signature was computed over.
/// </summary>
/// <remarks>
/// <para>
/// The hash fields are carried rather than recomputed at verification time so the sealer never
/// has to replicate the signing implementation's hash encoding — a silent mismatch there would
/// make every verification fail closed and strand every parked record. Carrying them is not a
/// weakness: the signature covers those fields, so editing a stored hash to match a forged
/// payload invalidates the signature.
/// </para>
/// <para>
/// <b><see cref="InputHash"/> binds the record's identity.</b> It is the hash of the subject id
/// (the escalation id or proposal id), which makes a seal valid for exactly one row. Without
/// that binding a seal is portable: an attacker could copy a legitimately approved record's
/// payload and seal verbatim into a different row, and every byte would still verify.
/// </para>
/// </remarks>
/// <param name="Signature">The signature over the sealed payload.</param>
/// <param name="KeyVersion">The version identifier of the signing key.</param>
/// <param name="SignedAt">When the seal was produced. Part of the signed payload.</param>
/// <param name="InputHash">The signing implementation's hash of the subject id this seal is bound to.</param>
/// <param name="OutputHash">The signing implementation's hash of the sealed payload.</param>
public sealed record GovernanceRecordSeal(
    string Signature,
    string KeyVersion,
    DateTimeOffset SignedAt,
    string InputHash,
    string OutputHash);

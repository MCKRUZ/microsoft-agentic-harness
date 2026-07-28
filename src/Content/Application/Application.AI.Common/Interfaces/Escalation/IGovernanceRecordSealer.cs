namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// Seals a persisted governance record — a resolved escalation outcome or a change proposal —
/// with a tamper-evident signature bound to that record's identity, and verifies the seal
/// before the record is ever acted upon again.
/// </summary>
/// <remarks>
/// <para>
/// The threat this closes: the durable governance-state database is a file. The reconciler
/// re-drives a stored outcome into <see cref="IEscalationAuditStore.RecordOutcomeAsync"/> — the
/// hash-chained compliance log — and the plan executor branches a human gate on a stored
/// verdict. Without a seal, flipping <c>"IsApproved": true</c> in a row would launder a forged
/// human approval into that log with a valid chain hash. The chain proves the log was not
/// edited after the fact; it cannot prove what was fed in.
/// </para>
/// <para>
/// <b>Every seal is bound to its record's id via <c>subjectId</c>.</b> Sealing the payload alone
/// is not enough: the payload of a legitimately approved record could be copied verbatim into
/// another row and would verify byte-for-byte, so one real approval would approve everything
/// after it. Binding the id makes a seal valid for exactly one row. Callers must additionally
/// check that the identity <em>inside</em> the deserialized payload matches the row it was
/// loaded from — the two checks close different halves of the same hole.
/// </para>
/// <para>
/// The default implementation delegates to the same HMAC key material the sandbox attestation
/// path uses (User Secrets / Key Vault, never appsettings), so enabling durable governance
/// state carries the attestation keys as a documented prerequisite. Verification is
/// fail-closed: a record whose seal is absent, malformed, non-matching, or bound to a different
/// subject is quarantined rather than used.
/// </para>
/// </remarks>
public interface IGovernanceRecordSealer
{
    /// <summary>
    /// Produces a seal over the exact serialized payload about to be persisted, bound to the
    /// record's identity.
    /// </summary>
    /// <param name="subjectId">
    /// The record's stable identity — an escalation id or a change-proposal id. Becomes part of
    /// the signed payload, so the resulting seal is valid only for this record.
    /// </param>
    /// <param name="payloadJson">The serialized record, byte-for-byte as it will be stored.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The seal to persist alongside the payload.</returns>
    Task<GovernanceRecordSeal> SealAsync(string subjectId, string payloadJson, CancellationToken ct);

    /// <summary>
    /// Verifies that <paramref name="payloadJson"/> is exactly what <paramref name="seal"/> was
    /// produced over, <em>and</em> that the seal was issued for <paramref name="subjectId"/>.
    /// </summary>
    /// <param name="subjectId">The identity of the record being verified.</param>
    /// <param name="payloadJson">The payload as currently stored.</param>
    /// <param name="seal">The persisted seal, or null when the row carries none.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// True only when a seal is present, was issued for this subject, and verifies against this
    /// exact payload. A null, mis-bound, or unverifiable seal returns false — never treated as
    /// "not sealed, therefore fine".
    /// </returns>
    Task<bool> VerifyAsync(string subjectId, string payloadJson, GovernanceRecordSeal? seal, CancellationToken ct);
}

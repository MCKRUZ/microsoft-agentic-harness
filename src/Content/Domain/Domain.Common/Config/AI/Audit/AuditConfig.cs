namespace Domain.Common.Config.AI.Audit;

/// <summary>
/// Configuration for the scheduled audit-chain verification service, which periodically walks
/// every hash-chained audit log and alerts when one has been tampered with.
/// </summary>
/// <remarks>
/// Without a scheduled verification pass the hash-chain catches tampering only when a record is
/// read; the periodic job is what turns the cryptographic guarantee into an operational alert.
/// On by default because audit integrity is a compliance-critical property of this template;
/// set <see cref="VerificationEnabled"/> to <c>false</c> to opt out.
/// </remarks>
public sealed class AuditConfig
{
    /// <summary>
    /// Gets whether the scheduled audit-chain verification background service runs. On by default.
    /// </summary>
    public bool VerificationEnabled { get; init; } = true;

    /// <summary>
    /// Gets the interval between full verification passes. Defaults to 24 hours.
    /// </summary>
    public TimeSpan VerificationInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets the delay before the first verification pass after startup, so boot is not blocked by
    /// a full chain walk. Defaults to 5 minutes.
    /// </summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the directory where per-run verification receipts are written as JSONL. When empty,
    /// receipts are not persisted (results are still logged and emitted as metrics).
    /// Defaults to <c>data/audit/audit-verify</c>.
    /// </summary>
    public string ReceiptPath { get; init; } = Path.Combine("data", "audit", "audit-verify");
}

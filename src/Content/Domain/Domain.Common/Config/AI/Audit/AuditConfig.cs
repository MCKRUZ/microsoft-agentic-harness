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

    /// <summary>
    /// Gets the folder where finished audit day-files are archived, or empty (the default) when nothing is archived.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The application never writes here. A separate job, under an identity the application does not hold, copies
    /// each audit log's finished day-files (never a log's newest file) into <c>{ArchivePath}/{log name}/</c> under
    /// their own names, checks the copy, and only then deletes the live file. The folder is mounted into the
    /// application read-only, typically an Azure Blob container under a locked time-based retention policy, mounted
    /// with <c>blobfuse2 --read-only</c> (#416).
    /// </para>
    /// <para>
    /// With it set, verification reads the archive's newest file of each log plus the live files, so a log whose
    /// oldest days have been archived still verifies, and readers serve archived and live records together. Must not
    /// overlap any audit log folder or <see cref="ReceiptPath"/>.
    /// </para>
    /// </remarks>
    public string ArchivePath { get; init; } = string.Empty;
}

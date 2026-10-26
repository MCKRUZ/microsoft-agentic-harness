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
    /// The application never writes here. The audit archiver (<c>Presentation.AuditArchiver</c>), run as a separate
    /// job under an identity the application does not hold, copies each audit log's finished day-files into
    /// <c>{ArchivePath}/{log folder}/</c> under their own names, but only files that continue the archive exactly, and
    /// records the log's newest record as a witness the application cannot forge. The folder is mounted into the
    /// application read-only, typically an Azure Blob container under a locked time-based retention policy, mounted
    /// with <c>blobfuse2 --read-only</c> and a read-only credential (#416).
    /// </para>
    /// <para>
    /// With it set, verification reads the live files from where they continue the archive, against the archiver's
    /// newest witness, and readers serve archived and live records together. Must not overlap any audit log folder or
    /// <see cref="ReceiptPath"/>.
    /// </para>
    /// </remarks>
    public string ArchivePath { get; init; } = string.Empty;

    /// <summary>
    /// Gets the base64 key the audit trails' pseudonyms are made with: the reference that stands for a person (an approver,
    /// a reviewer, a caller) and the fingerprint that stands for tool arguments (#903). At least 32 bytes. Empty (the
    /// default) uses the key in <see cref="PseudonymKeyPath"/>, made on first use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The trails are kept for as long as their hash chain must stay verifiable and cannot be edited, so they record a
    /// keyed reference rather than the name itself. Whoever holds the key can still answer "did this person approve that
    /// call?" by computing the person's reference, which is the accountability the trail exists for; nobody without it can
    /// reverse a reference or confirm a guess.
    /// </para>
    /// <para>
    /// Set it from a secret store wherever more than one instance writes the same trails, so every instance makes the same
    /// reference for the same person. Changing it starts new references; earlier records keep the key id they were made
    /// under, so they are never mistaken for references under the new key.
    /// </para>
    /// </remarks>
    public string PseudonymKey { get; init; } = string.Empty;

    /// <summary>
    /// Gets the file that holds the audit pseudonym key when <see cref="PseudonymKey"/> is empty. Made on first use with
    /// owner-only access, and read on every later start so references stay the same across restarts. Defaults to
    /// <c>.agent-sessions/keys/audit-pseudonym.key</c>.
    /// </summary>
    /// <remarks>
    /// Must not be inside any audit log folder, <see cref="ReceiptPath"/> or <see cref="ArchivePath"/>: the key must not
    /// travel with the records it protects when they are copied to the archive or handed out in a compliance report.
    /// </remarks>
    public string PseudonymKeyPath { get; init; } = Path.Combine(".agent-sessions", "keys", "audit-pseudonym.key");

    /// <summary>Gets the audit archiver's own settings. Read only by the archiver, never by the application.</summary>
    public AuditArchiverConfig Archiver { get; init; } = new();
}

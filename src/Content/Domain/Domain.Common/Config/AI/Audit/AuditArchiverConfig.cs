namespace Domain.Common.Config.AI.Audit;

/// <summary>
/// Settings for the audit archiver (#416), which moves finished audit day-files into the write-once archive at
/// <see cref="AuditConfig.ArchivePath"/> and witnesses each log's newest record. Read only by the archiver host; keep
/// the archiver's configuration where the application cannot change it.
/// </summary>
public sealed class AuditArchiverConfig
{
    /// <summary>
    /// Gets the Azure Blob container the archiver writes, e.g. <c>https://account.blob.core.windows.net/audit-archive</c>,
    /// authenticated with the archiver's own managed identity. Empty: the archiver writes <see cref="AuditConfig.ArchivePath"/>
    /// as a folder, which suits only tests and on-premises write-once volumes.
    /// </summary>
    public string BlobContainerUri { get; init; } = string.Empty;

    /// <summary>Gets how many days old (by file name) a day-file must be before it is archived. Defaults to 2.</summary>
    public int MinimumAgeDays { get; init; } = 2;

    /// <summary>
    /// Gets how long an archived file must have been in the archive before its live copy is deleted, so the
    /// application's mount, which may cache listings, already shows it. Defaults to 15 minutes.
    /// </summary>
    public TimeSpan DeleteDelay { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Gets the largest file the archiver reads, in megabytes. A larger one stops the run. Defaults to 256.</summary>
    public int MaxFileMegabytes { get; init; } = 256;
}

namespace Domain.Common.Compliance;

/// <summary>One store's line in the manifest of a personal-data export (#902).</summary>
/// <param name="Store">The store, as a reviewer would name it (for example <c>conversations</c>).</param>
/// <param name="Status">What happened.</param>
/// <param name="Records">How many records the store returned; zero unless <see cref="ExportStatus.Included"/>.</param>
/// <param name="Reason">
/// Why the status is not <see cref="ExportStatus.Included"/>; a stable, scrubbed phrase, never exception text, because
/// the manifest is returned to the caller.
/// </param>
public sealed record ExportManifestEntry(string Store, ExportStatus Status, int Records, string? Reason = null)
{
    /// <summary>Whether this line means the copy may be missing some of the person's data.</summary>
    public bool LeavesDataOut => Status is ExportStatus.Failed or ExportStatus.NotExportable or ExportStatus.RetainedUnsearchable;
}

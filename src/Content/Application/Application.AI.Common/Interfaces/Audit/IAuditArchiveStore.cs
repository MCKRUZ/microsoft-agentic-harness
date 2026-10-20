using Domain.AI.Audit;

namespace Application.AI.Common.Interfaces.Audit;

/// <summary>
/// The write-once archive that the audit archiver moves finished audit log files into (#416), as the archiver sees
/// it. Only the archiver uses this; the application reads the archive through a read-only mount and never writes it.
/// </summary>
/// <remarks>
/// <para>
/// Paths are relative to the archive's root and use <c>/</c>. A file is written exactly once and whole: it either
/// exists with all of its bytes or does not exist, so a run that stops half-way never leaves a partial file in storage
/// that refuses to replace it. Listing must be strongly consistent: a file just written is listed.
/// </para>
/// <para>
/// The production store is Azure Blob Storage with a locked time-based retention policy; a file-system store serves
/// tests and on-premises write-once volumes.
/// </para>
/// </remarks>
public interface IAuditArchiveStore
{
    /// <summary>The files directly in <paramref name="folder"/> (not in folders below it), in no particular order.</summary>
    /// <param name="folder">A folder path, or empty for the root.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The files; empty when the folder does not exist.</returns>
    Task<IReadOnlyList<AuditArchiveItem>> ListAsync(string folder, CancellationToken cancellationToken);

    /// <summary>Reads a file whole.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Its bytes, or <c>null</c> when it does not exist.</returns>
    Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>Writes a file that does not exist yet, whole, in one step.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="content">Its bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="AuditArchivePut.Created"/>; or, when a file is already there, <see cref="AuditArchivePut.Identical"/>
    /// or <see cref="AuditArchivePut.Different"/> by comparing its bytes.
    /// </returns>
    Task<AuditArchivePut> PutIfAbsentAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

using Application.AI.Common.Interfaces.Audit;
using Domain.AI.Audit;
using Infrastructure.AI.Helpers;

namespace Infrastructure.AI.Audit;

/// <summary>
/// An <see cref="IAuditArchiveStore"/> on a folder (#416): for tests, and for an on-premises volume that refuses to
/// change or delete a file once written. Not for a cloud share mounted as a folder: a mount can cache what it lists and
/// reads, and does not write a file in one step; the archiver host's Blob Storage store is for that.
/// </summary>
/// <remarks>
/// A file is written under a temporary name in the same folder and then renamed into place without replacing anything,
/// so it appears whole or not at all. A temporary file left by a crash is never listed as an archived file: its name
/// is not one the archive uses.
/// </remarks>
public sealed class FileAuditArchiveStore : IAuditArchiveStore
{
    private const string TemporaryPrefix = ".partial-";
    private readonly string _root;

    /// <summary>Initializes the store.</summary>
    /// <param name="root">The archive's root folder.</param>
    public FileAuditArchiveStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditArchiveItem>> ListAsync(string folder, CancellationToken cancellationToken)
    {
        var directory = Resolve(folder);
        IReadOnlyList<AuditArchiveItem> items = !Directory.Exists(directory)
            ? []
            : new DirectoryInfo(directory).GetFiles()
                .Where(f => !f.Name.StartsWith(TemporaryPrefix, StringComparison.Ordinal))
                .Select(f => new AuditArchiveItem(f.Name, f.Length, f.LastWriteTimeUtc))
                .ToList();
        return Task.FromResult(items);
    }

    /// <inheritdoc />
    public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var file = Resolve(path);
        return File.Exists(file) ? await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <inheritdoc />
    public async Task<AuditArchivePut> PutIfAbsentAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var target = Resolve(path);
        if (File.Exists(target))
            return await CompareAsync(target, content, cancellationToken).ConfigureAwait(false);

        var folder = Path.GetDirectoryName(target)!;
        // Owner-only like the live audit folders (#640): the archive holds the same records.
        OwnerOnlyDirectoryHelper.Create(folder);
        var temporary = Path.Combine(folder, $"{TemporaryPrefix}{Guid.NewGuid():N}");
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        try
        {
            File.Move(temporary, target, overwrite: false);
            return AuditArchivePut.Created;
        }
        catch (IOException) when (File.Exists(target))
        {
            // Another run got there first.
            TryDelete(temporary);
            return await CompareAsync(target, content, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<AuditArchivePut> CompareAsync(string target, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var existing = await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false);
        return existing.AsSpan().SequenceEqual(content.Span) ? AuditArchivePut.Identical : AuditArchivePut.Different;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A write-once volume may keep it; it is never listed.
        }
    }

    /// <summary>The full path for an archive path, which must stay inside the root.</summary>
    private string Resolve(string relative)
    {
        var root = Path.GetFullPath(_root);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal) && !string.Equals(full, Path.TrimEndingDirectorySeparator(root), StringComparison.Ordinal))
            throw new ArgumentException($"Archive path '{relative}' is outside the archive.", nameof(relative));
        return full;
    }
}

using System.Diagnostics;
using System.Security.Cryptography;

namespace Infrastructure.AI.Audit;

/// <summary>What <see cref="AuditArchiver"/> reads before it acts: the archive's side of a log, and its live files.</summary>
public sealed partial class AuditArchiver
{
    /// <summary>A file in a log's archive folder.</summary>
    /// <param name="Path">Its path in the archive.</param>
    /// <param name="Name">Its name, the same as its live file's.</param>
    /// <param name="LastModified">When it was archived.</param>
    private sealed record ArchivedFile(string Path, string Name, DateTimeOffset LastModified);

    /// <summary>A log as the archive holds it.</summary>
    /// <param name="Files">Its archived files, in chain order.</param>
    /// <param name="Tail">The newest archived record, or (-1, genesis).</param>
    /// <param name="Witnessed">The witnesses past the tail, by sequence.</param>
    /// <param name="Accepted">Accepted files' SHA-256, by file name.</param>
    private sealed record ArchiveState(
        IReadOnlyList<ArchivedFile> Files,
        (long Sequence, string Hash) Tail,
        IReadOnlyDictionary<long, string> Witnessed,
        IReadOnlyDictionary<string, string> Accepted)
    {
        /// <summary>The newest witnessed sequence past the tail, or <c>null</c>.</summary>
        public long? NewestWitness => Witnessed.Count == 0 ? null : Witnessed.Keys.Max();
    }

    /// <summary>Reads a log's archive folder: its files, tail, witnesses past the tail and acceptances.</summary>
    private async Task<ArchiveState> ReadArchiveAsync(AuditChainLocation chain, CancellationToken cancellationToken)
    {
        var folder = FolderOf(chain);
        var files = new List<ArchivedFile>();
        if (chain.Segments.LegacyFile is { } legacy
            && (await _store.ListAsync(string.Empty, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(i => i.Name == Path.GetFileName(legacy)) is { } legacyItem)
        {
            files.Add(new ArchivedFile(legacyItem.Name, legacyItem.Name, legacyItem.LastModified));
        }

        var items = await _store.ListAsync(folder, cancellationToken).ConfigureAwait(false);
        foreach (var item in items.Where(i => i.Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
        {
            if (DatedAuditSegments.DayOf(item.Name) is null)
                throw new IOException($"The archive folder {folder} holds {item.Name}, which is not a day-file name.");
        }

        files.AddRange(items
            .Where(i => DatedAuditSegments.DayOf(i.Name) is not null)
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .Select(i => new ArchivedFile($"{folder}/{i.Name}", i.Name, i.LastModified)));

        var tail = await TailAsync(files, cancellationToken).ConfigureAwait(false);
        var witnessed = await ReadWitnessesAsync(chain, tail.Sequence, cancellationToken).ConfigureAwait(false);

        var accepted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in await _store.ListAsync($"{folder}/{AuditAcceptance.Folder}", cancellationToken).ConfigureAwait(false))
        {
            var bytes = await _store.ReadAsync($"{folder}/{AuditAcceptance.Folder}/{item.Name}", cancellationToken).ConfigureAwait(false);
            if (bytes is not null && AuditAcceptance.From(bytes) is { } acceptance && item.Name == acceptance.File + ".json")
                accepted[acceptance.File] = acceptance.Sha256;
        }

        return new ArchiveState(files, tail, witnessed, accepted);
    }

    /// <summary>A log's witnesses above <paramref name="aboveSequence"/>, by sequence (<c>-1</c> for all of them).</summary>
    private async Task<IReadOnlyDictionary<long, string>> ReadWitnessesAsync(
        AuditChainLocation chain, long aboveSequence, CancellationToken cancellationToken)
    {
        var folder = $"{FolderOf(chain)}/{AuditWitness.Folder}";
        var witnessed = new Dictionary<long, string>();
        foreach (var item in await _store.ListAsync(folder, cancellationToken).ConfigureAwait(false))
        {
            if (!AuditWitness.IsFileName(item.Name)
                || long.Parse(item.Name[..19], System.Globalization.CultureInfo.InvariantCulture) <= aboveSequence)
            {
                continue;
            }

            var bytes = await _store.ReadAsync($"{folder}/{item.Name}", cancellationToken).ConfigureAwait(false);
            var witness = bytes is null ? null : AuditWitness.From(bytes);
            if (witness is null || AuditWitness.FileName(witness.Sequence) != item.Name)
                throw new IOException($"Witness {item.Name} in {folder} is not one this archiver wrote.");
            witnessed[witness.Sequence] = witness.Hash;
        }

        return witnessed;
    }

    /// <summary>The newest self-consistent archived record, skipping files with none; (-1, genesis) when there is none.</summary>
    private async Task<(long Sequence, string Hash)> TailAsync(IReadOnlyList<ArchivedFile> files, CancellationToken cancellationToken)
    {
        for (var i = files.Count - 1; i >= 0; i--)
        {
            var bytes = await _store.ReadAsync(files[i].Path, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException($"Archived file {files[i].Path} is listed but cannot be read.");
            if (!StrictAuditBytes.TryRead(bytes, out var lines, out var problem))
                throw new IOException($"Archived file {files[i].Path}: {problem}.");
            if (HashChainedJsonlWriter.LastSelfConsistentRecord(lines) is { } record)
                return record;
        }

        return (-1, HashChainedJsonlWriter.GenesisHash);
    }

    /// <summary>
    /// A live file's bytes, refusing anything but a plain file within the size limit: the live folder is controlled by
    /// the application's identity, which could plant a link to elsewhere or something that never ends.
    /// </summary>
    private async Task<byte[]> ReadLiveAsync(string path, CancellationToken cancellationToken)
    {
        RefuseLinks(path);

        // One handle, read to the limit and one byte more: a file swapped or grown after any check, or something that
        // never ends, is caught by what is actually read, not by what a path said a moment earlier.
        var limit = (long)_config.MaxFileMegabytes * 1024 * 1024;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit + 1 - buffer.Length)), cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
                throw new IOException($"{path} is larger than {_config.MaxFileMegabytes} MB.");
        }

        return buffer.ToArray();
    }

    /// <summary>Refuses a live file, or its folder, that is a link: the application's identity could point it elsewhere.</summary>
    private static void RefuseLinks(string path)
    {
        var file = new FileInfo(path);
        var folder = file.Directory!;
        if (!file.Exists)
            throw new FileNotFoundException($"{path} is gone.", path); // a missing file reads as every attribute set

        if (folder.LinkTarget is not null || folder.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"{path} or its folder is a link; refusing to follow it.");
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> holding the log's lock, the same one its writers take, so nothing is appended
    /// meanwhile. Opens the lock file only if it exists: creating it here could leave it owned by this account.
    /// Returns <c>false</c> without running it when there is no lock file.
    /// </summary>
    private static async Task<bool> UnderLockAsync(AuditChainLocation chain, Func<Task> action, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        FileStream? lockFile = null;
        while (lockFile is null)
        {
            try
            {
                lockFile = new FileStream(chain.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }
            catch (IOException) when (waited.Elapsed < LockTimeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }

        await using (lockFile)
        {
            await action().ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>The log's folder in the archive: named like its live day folder.</summary>
    private static string FolderOf(AuditChainLocation chain) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(chain.Segments.Folder));

    /// <summary>Where a live file goes in the archive: the earlier single file at the root, day-files in the log's folder.</summary>
    private static string ArchivePathOf(AuditChainLocation chain, string liveFile) =>
        DatedAuditSegments.DayOf(liveFile) is null ? Path.GetFileName(liveFile) : $"{FolderOf(chain)}/{Path.GetFileName(liveFile)}";

    private static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

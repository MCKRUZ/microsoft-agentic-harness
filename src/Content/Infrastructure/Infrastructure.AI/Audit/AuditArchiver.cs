using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Moves an audit log's finished day-files into a write-once archive, but only a file whose every record continues
/// the archive's newest one (#416). Run on a schedule by <c>Presentation.AuditArchiver</c>, under an identity that is
/// not the application's.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the check is here.</strong> The application's identity controls the live folders, so it can delete or
/// rewrite a day before that day is archived. Write-once storage keeps what it is given; it does not check it. So this
/// archiver checks every file before copying it: the first archived file starts the chain, and each later one carries
/// on exactly from the archive's newest record. A file that does not stops the run for that log, nothing more is
/// archived, and the run reports it, so a deleted or edited day is never sealed into the archive as a gap. This is the
/// one check the application's identity cannot get round, because it runs elsewhere.
/// </para>
/// <para>
/// <strong>What it does to the live folder.</strong> Reads files, takes the log's lock file (only if it exists) while
/// it snapshots and later deletes one, and deletes archived files. It creates nothing there: it may run as a privileged
/// account (owner-only folders keep other accounts out), and a file it created would lock the application out of its
/// own log.
/// </para>
/// <para>
/// <strong>Finished files only.</strong> Never a log's newest file, and never a day-file dated within the last
/// <see cref="MinimumAgeDays"/> days, so a host whose clock lags still finds its file in place. The earlier single
/// file counts as finished once a day-file that old exists. A file is copied from one snapshot of its bytes taken
/// under the log's lock, read back from the archive and compared, and deleted only if it still holds exactly those
/// bytes. A run that stops between copy and delete leaves a live copy of an archived file; the next run deletes it if
/// it is identical and stops if it is not.
/// </para>
/// </remarks>
public sealed partial class AuditArchiver
{
    /// <summary>How many days old (by file name) a day-file must be before it is archived.</summary>
    public const int MinimumAgeDays = 2;

    /// <summary>How long to wait for a writer to release a log's lock.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private readonly string _archiveRoot;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuditArchiver> _logger;

    /// <summary>Initializes the archiver.</summary>
    /// <param name="archiveRoot">The archive every audit log's files go into (<c>AppConfig:AI:Audit:ArchivePath</c>).</param>
    /// <param name="clock">Decides which day-files are old enough to archive.</param>
    /// <param name="logger">Logger for what was archived and why a run stopped.</param>
    public AuditArchiver(string archiveRoot, TimeProvider clock, ILogger<AuditArchiver> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveRoot);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _archiveRoot = archiveRoot;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Marks the archive ready by writing its marker file, which the application requires before it reads the
    /// archive. Run once, after the archive is mounted; does nothing when the marker is already there.
    /// </summary>
    /// <returns>Whether the marker was written now.</returns>
    public bool Initialize()
    {
        var sentinel = Path.Combine(_archiveRoot, HashChainedJsonlWriter.ArchiveSentinelFileName);
        if (File.Exists(sentinel))
            return false;

        Directory.CreateDirectory(_archiveRoot);
        using (var stream = new FileStream(sentinel, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes("Audit archive for hash-chained audit logs (#416). Do not remove.\n"));
        }

        _logger.LogInformation("Audit archive at {ArchiveRoot} marked ready.", _archiveRoot);
        return true;
    }

    /// <summary>Archives one log's finished files, in order, stopping at the first one that does not continue the archive.</summary>
    /// <param name="chain">The log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was archived, or why the run stopped.</returns>
    public async Task<AuditArchiveResult> ArchiveAsync(AuditChainLocation chain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (NotReady(chain) is { } notReady)
            return notReady;

        var archived = 0;
        try
        {
            var archive = chain.Segments.ArchivedUnder(_archiveRoot);
            var archivedFiles = archive.Ordered().ToList();
            var tail = await TailOfAsync(archivedFiles, cancellationToken).ConfigureAwait(false);
            if (tail is null)
                return Stop(chain, 0, $"Archived file {Path.GetFileName(archivedFiles[^1])} does not end with a whole record.");

            var live = chain.Segments.Ordered();
            if (await RemoveArchivedCopiesAsync(chain, live, archivedFiles, cancellationToken).ConfigureAwait(false) is { } copyProblem)
                return Stop(chain, 0, copyProblem);

            var candidates = archivedFiles.Count == 0
                ? live.ToList()
                : live.Where(p => chain.Segments.Compare(p, archivedFiles[^1]) > 0).ToList();

            var head = tail.Value;
            // Never the newest file: a writer may still append to it.
            for (var i = 0; i < candidates.Count - 1 && IsFinished(candidates[i], candidates); i++)
            {
                var allowPreChainLines = archivedFiles.Count == 0 && head.Sequence < 0;
                var outcome = await ArchiveOneAsync(chain, archive, candidates[i], head, allowPreChainLines, cancellationToken)
                    .ConfigureAwait(false);
                if (outcome.Problem is { } problem)
                    return Stop(chain, archived, problem);

                head = outcome.NewHead;
                archivedFiles.Add(Path.Combine(archive.Folder, Path.GetFileName(candidates[i])));
                archived++;
            }

            _logger.LogInformation("Audit log '{Chain}': archived {Count} file(s).", chain.Name, archived);
            return AuditArchiveResult.Done(chain.Name, archived);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Stop(chain, archived, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Copies one candidate into the archive and deletes it from the live folder, or says why not.</summary>
    private async Task<(string? Problem, (long Sequence, string Hash) NewHead)> ArchiveOneAsync(
        AuditChainLocation chain,
        DatedAuditSegments archive,
        string livePath,
        (long Sequence, string Hash) head,
        bool allowPreChainLines,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(livePath);
        byte[] snapshot;
        await using (await LockAsync(chain, cancellationToken).ConfigureAwait(false))
        {
            snapshot = await File.ReadAllBytesAsync(livePath, cancellationToken).ConfigureAwait(false);
        }

        if (snapshot.Length > 0 && snapshot[^1] != (byte)'\n')
            return ($"{name} does not end with a whole record.", head);

        var lines = Encoding.UTF8.GetString(snapshot).Split('\n');
        if (!HashChainedJsonlWriter.ContinuesChain(lines, head.Sequence, head.Hash, allowPreChainLines, out var newHead, out var reason))
        {
            return ($"{name} does not continue the archive's newest record (sequence {head.Sequence}): {reason} " +
                    "Records were removed or changed before archiving; investigate before archiving anything more.", head);
        }

        Directory.CreateDirectory(archive.Folder);
        var target = Path.Combine(archive.Folder, name);
        await using (var copy = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await copy.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
            await copy.FlushAsync(cancellationToken).ConfigureAwait(false);
            copy.Flush(flushToDisk: true);
        }

        var readBack = await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false);
        if (!readBack.AsSpan().SequenceEqual(snapshot))
        {
            return ($"The archived copy of {name} differs from what was written. The archive is write-once, so the " +
                    "copy cannot be replaced; the live file is kept.", head);
        }

        await using (await LockAsync(chain, cancellationToken).ConfigureAwait(false))
        {
            var current = await File.ReadAllBytesAsync(livePath, cancellationToken).ConfigureAwait(false);
            if (!current.AsSpan().SequenceEqual(snapshot))
                return ($"{name} changed while it was being archived; the live file is kept.", head);

            File.Delete(livePath);
        }

        _logger.LogInformation("Audit log '{Chain}': archived {File} (to sequence {Sequence}).", chain.Name, name, newHead.Sequence);
        return (null, newHead);
    }

    /// <summary>
    /// Deletes live files the archive already holds (a run that stopped between copy and delete). A live file sorting
    /// within the archive that is not an identical copy is a problem, returned.
    /// </summary>
    private async Task<string?> RemoveArchivedCopiesAsync(
        AuditChainLocation chain, IReadOnlyList<string> live, IReadOnlyList<string> archived, CancellationToken cancellationToken)
    {
        if (archived.Count == 0)
            return null;

        foreach (var path in live.Where(p => chain.Segments.Compare(p, archived[^1]) <= 0))
        {
            var name = Path.GetFileName(path);
            var copy = archived.FirstOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.Ordinal));
            if (copy is null)
                return $"Live file {name} sorts within the archive but the archive has no copy of it.";

            var archivedBytes = await File.ReadAllBytesAsync(copy, cancellationToken).ConfigureAwait(false);
            await using (await LockAsync(chain, cancellationToken).ConfigureAwait(false))
            {
                var liveBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (!liveBytes.AsSpan().SequenceEqual(archivedBytes))
                    return $"Live file {name} differs from its archived copy.";

                File.Delete(path);
            }

            _logger.LogInformation("Audit log '{Chain}': deleted {File}, already archived.", chain.Name, name);
        }

        return null;
    }

    /// <summary>Whether a candidate is old enough: a day-file by its date, the earlier single file by the oldest day-file's.</summary>
    private bool IsFinished(string path, IReadOnlyList<string> candidates)
    {
        var latest = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime).AddDays(-MinimumAgeDays);
        var day = DatedAuditSegments.DayOf(path)
            ?? candidates.Select(DatedAuditSegments.DayOf).FirstOrDefault(d => d is not null);
        return day is { } d2 && d2 <= latest;
    }

    /// <summary>The archive's newest record, (-1, genesis) when nothing is archived, or <c>null</c> when its newest file is torn.</summary>
    private static async Task<(long Sequence, string Hash)?> TailOfAsync(IReadOnlyList<string> archived, CancellationToken cancellationToken) =>
        archived.Count == 0
            ? (-1, HashChainedJsonlWriter.GenesisHash)
            : await HashChainedJsonlWriter.LastRecordAsync(archived[^1], cancellationToken).ConfigureAwait(false);

    private AuditArchiveResult? NotReady(AuditChainLocation chain) =>
        File.Exists(Path.Combine(_archiveRoot, HashChainedJsonlWriter.ArchiveSentinelFileName))
            ? null
            : AuditArchiveResult.NotReady(chain.Name,
                $"The archive at {_archiveRoot} has no marker file {HashChainedJsonlWriter.ArchiveSentinelFileName}: not " +
                "mounted, or not set up (run init once).");

    private AuditArchiveResult Stop(AuditChainLocation chain, int archived, string problem)
    {
        _logger.LogCritical("Audit log '{Chain}': archiving STOPPED after {Count} file(s): {Problem}", chain.Name, archived, problem);
        return AuditArchiveResult.Stopped(chain.Name, archived, problem);
    }

    /// <summary>
    /// Takes the log's lock, the same one its writers take, so no record is appended while a file is snapshotted or
    /// deleted. Opens the lock file only if it exists: creating it here could leave it owned by this account.
    /// </summary>
    private static async Task<FileStream> LockAsync(AuditChainLocation chain, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(chain.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException) && waited.Elapsed < LockTimeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

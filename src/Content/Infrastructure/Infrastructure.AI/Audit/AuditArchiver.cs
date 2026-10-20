using System.Text;
using Application.AI.Common.Interfaces.Audit;
using Domain.AI.Audit;
using Domain.Common.Config.AI.Audit;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Moves an audit log's finished day-files into a write-once archive, but only a file whose every record continues
/// the archive's newest one and matches every witnessed record (#416). Run on a schedule by
/// <c>Presentation.AuditArchiver</c>, as a separate job under an identity and with configuration the application
/// does not hold.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the checks are here.</strong> The application's identity controls the live folders, so it can delete
/// or rewrite records before they are archived, and recompute the unkeyed hashes. Write-once storage keeps what it is
/// given; it does not check it. So this archiver checks every file before copying it, and pins the live chain's head
/// with witnesses (<see cref="WitnessAsync"/>) that nothing on the application's hosts can rewrite. A file that does
/// not continue the archive, or contradicts a witness, stops the run for that log and nothing more is archived, so a
/// deleted or rewritten day is never sealed into the archive.
/// </para>
/// <para>
/// <strong>What it does to the live folder.</strong> Reads files (plain files only, within a size limit), takes the
/// log's lock file while it compares and deletes one (only if the lock file exists), and deletes files it has
/// archived. It creates nothing there: a file it created could be owned by an account the application cannot use.
/// </para>
/// <para>
/// <strong>Order of a run.</strong> Witness the head; delete live copies already archived for longer than
/// <see cref="AuditArchiverConfig.DeleteDelay"/> (so the application's mount already lists them); then archive
/// finished files in chain order: never a log's newest file, never a day-file younger than
/// <see cref="AuditArchiverConfig.MinimumAgeDays"/> days. Each is written whole, read back and compared; its live copy
/// is deleted by a later run. A run that stops part-way is safe to repeat.
/// </para>
/// </remarks>
public sealed partial class AuditArchiver
{
    /// <summary>How long to wait for a writer to release a log's lock.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private readonly IAuditArchiveStore _store;
    private readonly AuditArchiverConfig _config;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuditArchiver> _logger;

    /// <summary>Initializes the archiver.</summary>
    /// <param name="store">The write-once archive.</param>
    /// <param name="config">The archiver's settings.</param>
    /// <param name="clock">Decides which day-files are old enough to archive.</param>
    /// <param name="logger">Logger for what was archived and why a step stopped.</param>
    public AuditArchiver(IAuditArchiveStore store, AuditArchiverConfig config, TimeProvider clock, ILogger<AuditArchiver> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _config = config;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Marks the archive ready by writing its marker file, which the application requires before it reads the
    /// archive. Run once when the archive is set up; does nothing when the marker is already there.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the marker was written now.</returns>
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken)
    {
        var put = await _store.PutIfAbsentAsync(
            HashChainedJsonlWriter.ArchiveSentinelFileName,
            Encoding.UTF8.GetBytes("Audit archive for hash-chained audit logs (#416). Do not remove.\n"),
            cancellationToken).ConfigureAwait(false);
        if (put == AuditArchivePut.Created)
            _logger.LogInformation("Audit archive marked ready.");
        return put == AuditArchivePut.Created;
    }

    /// <summary>Witnesses one log, deletes its archived live copies, and archives its finished files.</summary>
    /// <param name="chain">The log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was archived, or why the run stopped.</returns>
    public async Task<AuditArchiveResult> ArchiveAsync(AuditChainLocation chain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var archived = 0;
        try
        {
            if (await NotReadyAsync(chain, cancellationToken).ConfigureAwait(false) is { } notReady)
                return notReady;

            var state = await ReadArchiveAsync(chain, cancellationToken).ConfigureAwait(false);
            var walk = await WitnessCoreAsync(chain, state, cancellationToken).ConfigureAwait(false);
            if (walk.Problem is not null)
                return Stop(chain, 0, walk.Problem);

            if (await RemoveArchivedCopiesAsync(chain, state, cancellationToken).ConfigureAwait(false) is { } copyProblem)
                return Stop(chain, 0, copyProblem);

            // Exactly the bytes the walk checked, file by file: nothing read later is archived unchecked.
            // Never the newest file: a writer may still append to it.
            for (var i = 0; i < walk.Files.Count - 1 && (FinishedEarly(walk.Files[i]) || IsFinished(walk.Files[i].Path)); i++)
            {
                var (problem, outcome) = await ArchiveOneAsync(chain, state, walk.Files[i], cancellationToken).ConfigureAwait(false);
                if (problem is not null)
                    return Stop(chain, archived, problem);
                if (outcome == FileOutcome.Archived)
                    archived++;
                if (outcome == FileOutcome.Kept)
                    break; // A later file archived past it would leave it sorting within the archive with no copy.
            }

            _logger.LogInformation("Audit log '{Chain}': archived {Count} file(s).", chain.Name, archived);
            return AuditArchiveResult.Done(chain.Name, archived);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Storage, authentication, anything: the log stops and the run exits 1, so the scheduler pages someone.
            return Stop(chain, archived, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>What happened to one candidate file.</summary>
    private enum FileOutcome
    {
        /// <summary>Copied into the archive.</summary>
        Archived,

        /// <summary>An empty file left by a crash, deleted rather than archived.</summary>
        Dropped,

        /// <summary>Neither: left in place, so nothing after it may be archived this run.</summary>
        Kept,
    }

    /// <summary>
    /// Copies one finished, checked file into the archive, or says why not. Its live copy is deleted by a later run,
    /// once the application's mount lists the archived one.
    /// </summary>
    private async Task<(string? Problem, FileOutcome Outcome)> ArchiveOneAsync(
        AuditChainLocation chain, ArchiveState state, CheckedFile file, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(file.Path);
        if (file.Bytes.Length == 0)
        {
            // Created by a writer that crashed before writing: no records, nothing to archive.
            var deleted = false;
            await UnderLockAsync(chain, () =>
            {
                RefuseLinks(file.Path);
                if (new FileInfo(file.Path).Length == 0)
                {
                    File.Delete(file.Path);
                    deleted = true;
                }

                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
            return (null, deleted ? FileOutcome.Dropped : FileOutcome.Kept);
        }

        // A witness written since this run read the archive (a witness job running alongside) must hold too.
        var witnessed = await ReadWitnessesAsync(chain, state.Tail.Sequence, cancellationToken).ConfigureAwait(false);
        if (Check(file, witnessed, newest: false) is { Problem: { } problem })
            return (problem, FileOutcome.Kept);

        var target = ArchivePathOf(chain, file.Path);
        if (await _store.PutIfAbsentAsync(target, file.Bytes, cancellationToken).ConfigureAwait(false) == AuditArchivePut.Different)
            return ($"The archive already holds a different {target}; it cannot be replaced.", FileOutcome.Kept);

        var readBack = await _store.ReadAsync(target, cancellationToken).ConfigureAwait(false);
        if (readBack is null || !readBack.AsSpan().SequenceEqual(file.Bytes))
            return ($"The archived copy of {name} reads back differently from what was written.", FileOutcome.Kept);

        _logger.LogInformation("Audit log '{Chain}': archived {File}{Accepted}.", chain.Name, name, file.Accepted ? ", as accepted" : string.Empty);
        return (null, FileOutcome.Archived);
    }

    /// <summary>
    /// Deletes live files the archive already holds, once their archived copy is old enough for the application's
    /// mount to list it. A live file sorting within the archive that is not an identical copy is a problem, returned.
    /// </summary>
    private async Task<string?> RemoveArchivedCopiesAsync(AuditChainLocation chain, ArchiveState state, CancellationToken cancellationToken)
    {
        if (state.Files.Count == 0)
            return null;

        var newest = LivePathOf(chain, state.Files[^1].Name);
        foreach (var path in chain.Segments.Ordered().Where(p => chain.Segments.Compare(p, newest) <= 0))
        {
            var name = Path.GetFileName(path);
            var copy = state.Files.FirstOrDefault(f => f.Name == name);
            if (copy is null)
                return $"Live file {name} sorts within the archive but the archive has no copy of it.";

            var archivedBytes = await _store.ReadAsync(copy.Path, cancellationToken).ConfigureAwait(false) ?? [];
            string? problem = null;
            async Task CompareThenDeleteAsync()
            {
                var liveBytes = await ReadLiveAsync(path, cancellationToken).ConfigureAwait(false);
                if (!liveBytes.AsSpan().SequenceEqual(archivedBytes))
                {
                    problem = $"Live file {name} differs from its archived copy.";
                }
                else if (_clock.GetUtcNow() - copy.LastModified >= _config.DeleteDelay)
                {
                    RefuseLinks(path); // again, just before deleting by path
                    File.Delete(path);
                }
            }

            if (!await UnderLockAsync(chain, CompareThenDeleteAsync, cancellationToken).ConfigureAwait(false))
            {
                // No lock file, so nothing is deleted; the copy is still compared.
                var liveBytes = await ReadLiveAsync(path, cancellationToken).ConfigureAwait(false);
                if (!liveBytes.AsSpan().SequenceEqual(archivedBytes))
                    problem = $"Live file {name} differs from its archived copy.";
                _logger.LogWarning("Audit log '{Chain}': no lock file at {Lock}; archived live files are kept.", chain.Name, chain.LockPath);
            }

            if (problem is not null)
                return problem;
        }

        return null;
    }

    /// <summary>
    /// Whether a candidate is old enough: a day-file by its date. The earlier single file is finished as soon as a
    /// day-file follows it; it, and any file that may hold lines from before hash chaining, is archived at once.
    /// </summary>
    private bool IsFinished(string path)
    {
        var latest = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime).AddDays(-_config.MinimumAgeDays);
        return DatedAuditSegments.DayOf(path) is not { } day || day <= latest;
    }

    private async Task<AuditArchiveResult?> NotReadyAsync(AuditChainLocation chain, CancellationToken cancellationToken) =>
        await _store.ReadAsync(HashChainedJsonlWriter.ArchiveSentinelFileName, cancellationToken).ConfigureAwait(false) is not null
            ? null
            : AuditArchiveResult.NotReady(chain.Name,
                $"The archive has no marker file {HashChainedJsonlWriter.ArchiveSentinelFileName}: not set up (run init once).");

    private AuditArchiveResult Stop(AuditChainLocation chain, int archived, string problem)
    {
        _logger.LogCritical("Audit log '{Chain}': archiver STOPPED after {Count} file(s): {Problem}", chain.Name, archived, problem);
        return AuditArchiveResult.Stopped(chain.Name, archived, problem);
    }
}

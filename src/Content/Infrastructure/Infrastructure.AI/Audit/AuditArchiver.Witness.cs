using Domain.AI.Audit;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>The witness step of <see cref="AuditArchiver"/>: pinning the live chain's head where the application cannot reach.</summary>
/// <remarks>
/// Records are hashed without a key, so whoever controls the live folder can rebuild the records not yet archived,
/// recompute every hash, and rewrite the application's own checkpoint to match. A witness, written into the write-once
/// archive by this archiver, is a checkpoint that cannot be rewritten: every later step requires the chain to hold the
/// same hash at the witnessed sequence. Run the witness step often (every 15 to 60 minutes); what is exposed is only
/// what was written since the last witness.
/// </remarks>
public sealed partial class AuditArchiver
{
    /// <summary>
    /// Walks one log's live chain from the archive's newest record, requires it to still hold every witnessed record,
    /// and witnesses its new head. Any line that does not chain stops it, apart from the unfinished last line of the
    /// newest file, which may be an append in progress.
    /// </summary>
    /// <param name="chain">The log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Done, or why the log must be investigated.</returns>
    public async Task<AuditArchiveResult> WitnessAsync(AuditChainLocation chain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (await NotReadyAsync(chain, cancellationToken).ConfigureAwait(false) is { } notReady)
            return notReady;

        try
        {
            var state = await ReadArchiveAsync(chain, cancellationToken).ConfigureAwait(false);
            var walk = await WitnessCoreAsync(chain, state, cancellationToken).ConfigureAwait(false);
            return walk.Problem is null ? AuditArchiveResult.Done(chain.Name) : Stop(chain, 0, walk.Problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Stop(chain, 0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The witness step on an archive already read: the walk it witnessed, or the problem that stopped it.</summary>
    private async Task<LiveWalk> WitnessCoreAsync(AuditChainLocation chain, ArchiveState state, CancellationToken cancellationToken)
    {
        var walk = await WalkLiveAsync(chain, state, cancellationToken).ConfigureAwait(false);
        if (walk.Problem is not null || walk.Head.Sequence <= Math.Max(state.Tail.Sequence, state.NewestWitness ?? -1))
            return walk;

        var witness = new AuditWitness(walk.Head.Sequence, walk.Head.Hash, walk.HeadFile!, _clock.GetUtcNow());
        var path = $"{FolderOf(chain)}/{AuditWitness.Folder}/{AuditWitness.FileName(witness.Sequence)}";
        var put = await _store.PutIfAbsentAsync(path, witness.ToBytes(), cancellationToken).ConfigureAwait(false);
        if (put == AuditArchivePut.Different)
        {
            // Another run witnessed this sequence at the same moment; its file differs only in the time, or in the hash.
            var existing = AuditWitness.From(await _store.ReadAsync(path, cancellationToken).ConfigureAwait(false) ?? []);
            if (existing?.Hash != witness.Hash)
                return walk with { Problem = $"A witness at sequence {witness.Sequence} already holds a different hash." };
        }

        _logger.LogInformation("Audit log '{Chain}': witnessed sequence {Sequence}.", chain.Name, witness.Sequence);
        return walk;
    }

    /// <summary>One live file as the walk read and checked it.</summary>
    /// <param name="Path">Its path.</param>
    /// <param name="Bytes">The bytes that were checked: the only bytes that may be archived for it.</param>
    private sealed record CheckedFile(string Path, byte[] Bytes);

    /// <summary>The outcome of walking the live chain.</summary>
    /// <param name="Problem">Why the walk stopped, or <c>null</c>.</param>
    /// <param name="Head">The newest record reached.</param>
    /// <param name="HeadFile">The file it is in.</param>
    /// <param name="Files">The files checked, in chain order, up to where the walk stopped.</param>
    private sealed record LiveWalk(string? Problem, (long Sequence, string Hash) Head, string? HeadFile, IReadOnlyList<CheckedFile> Files);

    /// <summary>
    /// The live chain after the archive, read strictly: where it reaches and in which file, or why it does not
    /// continue the archive or match a witness. The newest file's unfinished last line is left out; any other line
    /// that does not chain is a problem unless its file was accepted.
    /// </summary>
    private async Task<LiveWalk> WalkLiveAsync(AuditChainLocation chain, ArchiveState state, CancellationToken cancellationToken)
    {
        var live = AfterArchive(chain, state);
        var head = state.Tail;
        string? headFile = null;
        var checkedFiles = new List<CheckedFile>();
        for (var i = 0; i < live.Count; i++)
        {
            var name = Path.GetFileName(live[i]);
            var bytes = await ReadLiveAsync(live[i], cancellationToken).ConfigureAwait(false);
            if (CheckLiveFile(state, name, bytes, head, firstOfChain: state.Files.Count == 0 && i == 0, newest: i == live.Count - 1)
                is var (reached, problem) && problem is not null)
            {
                return new LiveWalk(problem, head, headFile, checkedFiles);
            }

            if (reached.Sequence > head.Sequence)
                headFile = name;
            head = reached;
            checkedFiles.Add(new CheckedFile(live[i], bytes));
        }

        if (state.NewestWitness is { } witnessed && head.Sequence < witnessed)
        {
            return new LiveWalk(
                $"The live chain ends at sequence {head.Sequence}, before the witnessed sequence {witnessed}: records were removed.",
                head, headFile, checkedFiles);
        }

        return new LiveWalk(null, head, headFile, checkedFiles);
    }

    /// <summary>
    /// Whether one live file's bytes continue the chain from <paramref name="head"/>: strict bytes, whole lines (the
    /// newest file's unfinished last line left out; another file's only if it was accepted with exactly these bytes),
    /// every record chaining and matching every witness. Returns where it reaches, or the problem.
    /// </summary>
    private static ((long Sequence, string Hash) Reached, string? Problem) CheckLiveFile(
        ArchiveState state, string name, byte[] bytes, (long Sequence, string Hash) head, bool firstOfChain, bool newest)
    {
        var accepted = state.Accepted.TryGetValue(name, out var sha) && sha == Sha256Of(bytes);
        if (!StrictAuditBytes.TryRead(bytes, out var lines, out var problem))
            return (head, $"Live file {name}: {problem}.");

        var whole = bytes.Length == 0 || bytes[^1] == (byte)'\n';
        if (!whole && newest)
            lines = lines.Take(lines.Count - 1).ToList(); // an append in progress
        else if (!whole && !accepted)
            return (head, $"Live file {name} does not end with a whole record (a torn write? see the archiver's accept command).");

        return HashChainedJsonlWriter.ContinuesChain(
                lines, head.Sequence, head.Hash, firstOfChain, accepted, state.Witnessed, out var reached, out var reason)
            ? (reached, null)
            : (head, $"Live file {name} does not continue the chain from sequence {head.Sequence}: {reason} " +
                     "Records were removed or changed; investigate before archiving anything more.");
    }

    /// <summary>The live files after the archive's newest file: the live chain.</summary>
    private static IReadOnlyList<string> AfterArchive(AuditChainLocation chain, ArchiveState state)
    {
        var live = chain.Segments.Ordered();
        return state.Files.Count == 0
            ? live
            : live.Where(p => chain.Segments.Compare(p, LivePathOf(chain, state.Files[^1].Name)) > 0).ToList();
    }

    /// <summary>Where a file of this name lives in the live area, for ordering it against live files.</summary>
    private static string LivePathOf(AuditChainLocation chain, string name) =>
        DatedAuditSegments.DayOf(name) is null && chain.Segments.LegacyFile is { } legacy
            ? legacy
            : Path.Combine(chain.Segments.Folder, name);
}

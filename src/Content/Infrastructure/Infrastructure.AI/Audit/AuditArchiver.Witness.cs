using Domain.AI.Audit;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>The witness step of <see cref="AuditArchiver"/>: pinning the live chain's head where the application cannot reach.</summary>
/// <remarks>
/// Records are hashed without a key, so whoever controls the live folder can rebuild the records not yet archived,
/// recompute every hash, and rewrite the application's own checkpoint to match. A witness, written into the write-once
/// archive by this archiver, is a checkpoint that cannot be rewritten: every later step requires the chain to still
/// hold the same hash at the witnessed sequence. Run the witness step often (every 15 to 60 minutes); what is exposed
/// is only what was written since the last witness.
/// </remarks>
public sealed partial class AuditArchiver
{
    /// <summary>
    /// Walks one log's live chain from the archive's newest record, requires it to still hold every witnessed record,
    /// and witnesses its new head. Any line that does not chain stops it, apart from the unfinished last line of the
    /// newest file, which may be an append in progress; how far the chain did verify is still witnessed.
    /// </summary>
    /// <param name="chain">The log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Done, or why the log must be investigated.</returns>
    public async Task<AuditArchiveResult> WitnessAsync(AuditChainLocation chain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chain);
        try
        {
            if (await NotReadyAsync(chain, cancellationToken).ConfigureAwait(false) is { } notReady)
                return notReady;

            var state = await ReadArchiveAsync(chain, cancellationToken).ConfigureAwait(false);
            var walk = await WitnessCoreAsync(chain, state, cancellationToken).ConfigureAwait(false);
            return walk.Problem is null ? AuditArchiveResult.Done(chain.Name) : Stop(chain, 0, walk.Problem);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Stop(chain, 0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The witness step on an archive already read: walks the live chain and witnesses as far as it verified, even
    /// when it then stopped, so a planted break cannot hold the witness back while later records are rewritten.
    /// </summary>
    private async Task<LiveWalk> WitnessCoreAsync(
        AuditChainLocation chain, ArchiveState state, CancellationToken cancellationToken, bool walkedAgain = false)
    {
        var walk = await WalkLiveAsync(chain, state, cancellationToken).ConfigureAwait(false);

        // Past the newest witness means the walk reached that witnessed record with the witnessed hash on the way.
        if (walk.HeadFile is null || walk.Head.Sequence <= Math.Max(state.Tail.Sequence, state.NewestWitness ?? -1))
            return walk;

        // A witness written by another job while this one walked was not checked: walk again against it, once.
        var current = await ReadWitnessesAsync(chain, state.Tail.Sequence, cancellationToken).ConfigureAwait(false);
        if (current.Keys.Any(sequence => !state.Witnessed.ContainsKey(sequence)))
        {
            if (walkedAgain)
            {
                _logger.LogWarning("Audit log '{Chain}': witnesses keep arriving from another job; not witnessing this time.", chain.Name);
                return walk;
            }

            return await WitnessCoreAsync(chain, state with { Witnessed = current }, cancellationToken, walkedAgain: true)
                .ConfigureAwait(false);
        }

        var witness = new AuditWitness(walk.Head.Sequence, walk.Head.Hash, walk.HeadFile, _clock.GetUtcNow());
        var path = $"{FolderOf(chain)}/{AuditWitness.Folder}/{AuditWitness.FileName(witness.Sequence)}";
        if (await _store.PutIfAbsentAsync(path, witness.ToBytes(), cancellationToken).ConfigureAwait(false) == AuditArchivePut.Different)
        {
            // Another run witnessed this sequence at the same moment; its file differs in the time, or in the hash.
            var existing = AuditWitness.From(await _store.ReadAsync(path, cancellationToken).ConfigureAwait(false) ?? []);
            if (existing?.Hash != witness.Hash)
                return walk with { Problem = walk.Problem ?? $"A witness at sequence {witness.Sequence} already holds a different hash." };
        }

        _logger.LogInformation("Audit log '{Chain}': witnessed sequence {Sequence}.", chain.Name, witness.Sequence);
        return walk;
    }

    /// <summary>One live file as the walk read and checked it.</summary>
    /// <param name="Path">Its path.</param>
    /// <param name="Bytes">The bytes that were checked: the only bytes that may be archived for it.</param>
    /// <param name="Start">The record it was checked to continue.</param>
    /// <param name="AllowPreChainLines">Whether it is the earlier single file starting the chain.</param>
    /// <param name="Accepted">Whether an operator accepted exactly these bytes.</param>
    private sealed record CheckedFile(string Path, byte[] Bytes, (long Sequence, string Hash) Start, bool AllowPreChainLines, bool Accepted);

    /// <summary>The outcome of walking the live chain.</summary>
    /// <param name="Problem">Why the walk stopped, or <c>null</c>.</param>
    /// <param name="Head">The newest record verified.</param>
    /// <param name="HeadFile">The file it is in, or <c>null</c> when the walk verified no new record.</param>
    /// <param name="Files">The files checked whole, in chain order, up to where the walk stopped.</param>
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
            var file = new CheckedFile(
                live[i], bytes, head,
                AllowPreChainLines: AllowsPreChainLines(chain, name, head),
                Accepted: state.Accepted.TryGetValue(name, out var sha) && sha == Sha256Of(bytes));
            var (reached, problem) = Check(file, state.Witnessed, newest: i == live.Count - 1);
            if (reached.Sequence > head.Sequence)
                headFile = name;
            head = reached;
            if (problem is not null)
                return new LiveWalk(problem, head, headFile, checkedFiles);

            checkedFiles.Add(file);
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
    /// Whether a live file's bytes continue the chain from its start: bytes the application reads the same way
    /// (strict, unless accepted), whole lines (the newest file's unfinished last line left out; another file's only if
    /// accepted), every record chaining and matching every witness. Returns how far it verified, and the problem.
    /// </summary>
    private static ((long Sequence, string Hash) Reached, string? Problem) Check(
        CheckedFile file, IReadOnlyDictionary<long, string> witnessed, bool newest)
    {
        var name = Path.GetFileName(file.Path);
        if (LinesOf(file) is not { } lines)
            return (file.Start, $"Live file {name}: {StrictProblem(file.Bytes)}.");

        var whole = file.Bytes.Length == 0 || file.Bytes[^1] == (byte)'\n';
        if (!whole && newest)
            lines = lines.Take(lines.Count - 1).ToList(); // an append in progress
        else if (!whole && !file.Accepted)
            return (file.Start, $"Live file {name} does not end with a whole record (a torn write? see the archiver's accept command).");

        return HashChainedJsonlWriter.ContinuesChain(
                lines, file.Start.Sequence, file.Start.Hash, file.AllowPreChainLines, file.Accepted, witnessed, out var reached, out var reason)
            ? (reached, null)
            : (reached, $"Live file {name} does not continue the chain from sequence {file.Start.Sequence}: {reason} " +
                        "Records were removed or changed; investigate before archiving anything more.");
    }

    /// <summary>
    /// A file's lines as the application reads them: strictly when not accepted (refusing what could read two ways),
    /// and, for an accepted file, with the same reader the application uses.
    /// </summary>
    private static IReadOnlyList<string>? LinesOf(CheckedFile file) =>
        file.Accepted ? ReadLikeTheApplication(file.Bytes)
        : StrictAuditBytes.TryRead(file.Bytes, out var lines, out _) ? lines
        : null;

    private static string StrictProblem(byte[] bytes)
    {
        StrictAuditBytes.TryRead(bytes, out _, out var problem);
        return problem!;
    }

    /// <summary>Lines exactly as <see cref="StreamReader"/>, which the application reads with, returns them.</summary>
    private static IReadOnlyList<string> ReadLikeTheApplication(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes));
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines;
    }

    /// <summary>
    /// Whether a file may hold lines written before hash chaining began: only before the chain's first record, and,
    /// for a log with an earlier single file, only in that file. A log split by day from the start (drift) has its
    /// older lines in its first day-files instead; those are archived on the first run (see <see cref="FinishedEarly"/>).
    /// </summary>
    private static bool AllowsPreChainLines(AuditChainLocation chain, string name, (long Sequence, string Hash) head) =>
        head.Sequence < 0 && (chain.Segments.LegacyFile is null || DatedAuditSegments.DayOf(name) is null);

    /// <summary>
    /// Whether a checked file is archived at once, whatever its age: one that may hold lines written before hash
    /// chaining began, which nothing covers until they are sealed in the archive.
    /// </summary>
    private static bool FinishedEarly(CheckedFile file) => file.AllowPreChainLines;

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

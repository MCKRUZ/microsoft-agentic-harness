using Domain.AI.Audit;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>
/// The archive half of <see cref="HashChainedJsonlWriter"/> (#416): a chain whose oldest files have been moved by the
/// audit archiver into an archive this application can read but not write.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Who moves files.</strong> Never this application. <see cref="AuditArchiver"/>, run on a schedule under an
/// identity of its own, copies a chain's finished files into the archive under their own names, but only a file whose
/// every record continues the archive's newest one; then it deletes the live file. So the archive holds one unbroken
/// chain from genesis, checked when each file went in, and nothing running as this application can add to it, change
/// it or remove from it: the archive is write-once storage mounted here read-only.
/// </para>
/// <para>
/// <strong>One seam rule.</strong> Verification, the writer's fallback walk and the readers all split the live files at
/// the archive's newest file. A live file sorting after it is the live chain, which must continue the archive's newest
/// record exactly (its first record carries the next sequence and points back to that record's hash). A live file
/// sorting at or before it is a copy the archiver has not deleted yet: it must be byte-identical to its archived copy,
/// is never read as live, and a file there with no archived copy is a break. With nothing archived, the live chain
/// starts at genesis, as it always did.
/// </para>
/// <para>
/// <strong>A checkpoint inside the archive.</strong> When verification last checked a record the archiver has since
/// archived, a pass walks back to the archived file holding it and reads from there, so the record is compared again.
/// A checkpoint before the archive's first record, or in a gap between archived files, is a break.
/// </para>
/// <para>
/// <strong>When the archive is not there.</strong> The archiver writes a marker file, <see cref="ArchiveSentinelFileName"/>,
/// into the archive once. Without it (a share that is not mounted, an archive never set up), or when the archive cannot
/// be read, appends fail and verification throws <see cref="AuditFolderRefusedException"/>: not verified, rather than
/// every archived record reported as deleted. Verification and the fallback walk also try to create a file in the
/// archive, which must fail. These catch a misconfigured mount; they cannot stop someone who controls this host from
/// faking an archive on a local folder. What this host cannot fake is the archiver's own refusal to archive a file
/// that does not continue the archive, and its full <c>verify</c> run under its own identity.
/// </para>
/// </remarks>
public sealed partial class HashChainedJsonlWriter
{
    /// <summary>The marker file the archiver writes once into a ready archive, at its root.</summary>
    public const string ArchiveSentinelFileName = ".audit-archive";

    private readonly DatedAuditSegments? _archive;
    private readonly string? _archiveRoot;

    /// <summary>The earlier single file's name, the only file that may hold lines written before the chain began.</summary>
    private readonly string? _legacyFileName;

    /// <summary>The probe file that showed the archive accepts writes from this application, once one has.</summary>
    private volatile string? _archiveAcceptedWrite;

    /// <summary>Initializes a chain from its shared location (<see cref="AuditChains"/>).</summary>
    /// <param name="chain">The chain's files and lock.</param>
    /// <param name="logger">Logger for operational diagnostics.</param>
    /// <param name="archivePath">The archive every audit log's finished files are moved into, or empty for none.</param>
    public HashChainedJsonlWriter(AuditChainLocation chain, ILogger logger, string? archivePath = null)
        : this((chain ?? throw new ArgumentNullException(nameof(chain))).Segments, chain.LockPath, logger, archivePath)
    {
    }

    /// <summary>
    /// Every file of the chain in chain order: the archived files, then the live files after the archive's newest one
    /// (with this writer's current file even when the listing missed it). What readers read.
    /// </summary>
    /// <returns>Full paths.</returns>
    /// <exception cref="AuditFolderRefusedException">
    /// An archive is configured but not ready or not readable, or a folder holds a refused file name.
    /// </exception>
    public IReadOnlyList<string> ChainFiles()
    {
        var live = WithCurrentSegment(_orderedSegments());
        if (_archive is null)
            return live;

        var archived = ListArchive(probe: false);
        return [.. archived, .. AfterArchive(live, archived)];
    }

    /// <summary>The archived files in chain order, after checking the archive is there (and, with <paramref name="probe"/>, not writable).</summary>
    private IReadOnlyList<string> ListArchive(bool probe)
    {
        var root = _archiveRoot!;
        try
        {
            if (!File.Exists(Path.Combine(root, ArchiveSentinelFileName)))
            {
                throw new AuditFolderRefusedException(
                    $"The audit archive at {root} is not ready: its marker file {ArchiveSentinelFileName} is missing (not " +
                    $"mounted, or never set up with the archiver's init). The chain at {_displayLocation} is not written " +
                    "or verified until it is, so that archived records are not taken for deleted ones.");
            }

            if (probe)
                RefuseWritableArchive(root);

            return _archive!.Ordered();
        }
        catch (Exception ex) when (ex is (IOException and not AuditFolderRefusedException) or UnauthorizedAccessException)
        {
            throw ArchiveUnreadable(ex);
        }
    }

    /// <summary>
    /// Creating a file in the archive must fail: this application only ever reads it. A file name used once, so an
    /// existing file can never pass for a refusal.
    /// </summary>
    private void RefuseWritableArchive(string root)
    {
        // Once writable, refused until restart without probing again: each probe could leave an undeletable file.
        if (_archiveAcceptedWrite is { } earlier)
            throw WritableArchive(root, earlier);

        var probe = Path.Combine(root, $".write-probe-{Guid.NewGuid():N}");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Refused, as it must be.
        }

        try
        {
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Expected on write-once storage; the message below says so.
        }

        _archiveAcceptedWrite = Path.GetFileName(probe);
        throw WritableArchive(root, _archiveAcceptedWrite);
    }

    private AuditFolderRefusedException WritableArchive(string root, string probeName) => new(
        $"The audit archive at {root} accepts writes from this application, so it could hold files this application " +
        $"put there. Mount it read-only with a read-only credential, then restart. The chain at {_displayLocation} is not " +
        $"written or verified until then. A test file, {probeName}, was created there and may be undeletable if the " +
        "archive is write-once.");

    /// <summary>The live files after the archive's newest file: the live chain.</summary>
    private IReadOnlyList<string> AfterArchive(IReadOnlyList<string> live, IReadOnlyList<string> archived) =>
        archived.Count == 0 ? live : live.Where(p => _segmentOrder(p, archived[^1]) > 0).ToList();

    /// <summary>
    /// For verification: the live files at or before the archive's newest file must each be a byte-identical copy of
    /// an archived file. Returns the break, or <c>null</c>.
    /// </summary>
    private async Task<ChainScan?> CompareCopiesAsync(
        IReadOnlyList<SegmentReach> live, IReadOnlyList<string> archived, CancellationToken cancellationToken)
    {
        if (archived.Count == 0)
            return null;

        foreach (var segment in live.Where(s => _segmentOrder(s.Path, archived[^1]) <= 0))
        {
            var name = Path.GetFileName(segment.Path);
            var copy = archived.FirstOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.Ordinal));
            if (copy is null)
            {
                return ScanState.Genesis.Break(0,
                    $"Live file {name} sorts within the archive (at or before {Path.GetFileName(archived[^1])}) but has " +
                    "no archived copy: it was not written by this chain's writers.");
            }

            // Gone since it was measured: the archiver deleted it after checking its copy.
            if (!File.Exists(segment.Path))
                continue;

            if (!await ArchiveReadAsync(() => SameBytesAsync(segment, copy, cancellationToken)).ConfigureAwait(false))
                return ScanState.Genesis.Break(0, $"Live file {name} differs from its archived copy.");
        }

        return null;
    }

    /// <summary>
    /// Where the live chain continues from: just after the archive's newest self-consistent record, skipping archived
    /// files that hold none (written before the chain began); genesis when there is none. The archiver uses the same rule.
    /// </summary>
    private async Task<ScanState> ArchiveTailAsync(IReadOnlyList<string> archived, CancellationToken cancellationToken)
    {
        for (var i = archived.Count - 1; i >= 0; i--)
        {
            var path = archived[i];
            if (await ArchiveReadAsync(() => LastRecordAsync(path, cancellationToken)).ConfigureAwait(false) is { } record)
                return ScanState.FromHead(record.Sequence, record.Hash);
        }

        return ScanState.Genesis;
    }

    /// <summary>The archiver's newest witness for this chain, as a checkpoint; <c>null</c> when there is none yet.</summary>
    private async Task<AuditChainCheckpoint?> NewestWitnessAsync(CancellationToken cancellationToken)
    {
        var folder = Path.Combine(_archive!.Folder, AuditWitness.Folder);
        return await ArchiveReadAsync(async () =>
        {
            if (!Directory.Exists(folder))
                return null;

            var newest = Directory.GetFiles(folder)
                .Select(Path.GetFileName)
                .Where(n => AuditWitness.IsFileName(n!))
                .Max(StringComparer.Ordinal);
            if (newest is null)
                return null;

            var witness = AuditWitness.From(await File.ReadAllBytesAsync(Path.Combine(folder, newest), cancellationToken).ConfigureAwait(false));
            if (witness is null || AuditWitness.FileName(witness.Sequence) != newest)
            {
                throw new AuditFolderRefusedException(
                    $"Audit archive witness {newest} for the chain at {_displayLocation} is not a witness the archiver wrote. " +
                    "The chain is not verified until it is investigated.");
            }

            return (AuditChainCheckpoint?)new AuditChainCheckpoint(witness.Sequence, witness.Hash);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// For a checkpoint the archiver has since archived: the archived files from the one holding it, with the state
    /// that reads them, or the break that rules it out.
    /// </summary>
    private async Task<(IReadOnlyList<SegmentReach> Files, ScanState Start, ChainScan? Broken)> WalkBackAsync(
        AuditChainCheckpoint checkpoint, IReadOnlyList<string> archived, CancellationToken cancellationToken)
    {
        for (var i = archived.Count - 1; i >= 0; i--)
        {
            var first = await ArchiveReadAsync(() => FirstRecordAsync(archived[i], cancellationToken)).ConfigureAwait(false);
            if (first is { } record && record.Sequence <= checkpoint.Sequence)
            {
                var files = archived.Skip(i).Select(p => new SegmentReach(p, null, Archived: true)).ToList();
                return (files, ScanState.FromHead(record.Sequence - 1, record.PreviousHash), null);
            }
        }

        return ([], ScanState.Genesis, ScanState.Genesis.Break(checkpoint.Sequence,
            $"Record at sequence {checkpoint.Sequence}, last verified, comes before every archived record."));
    }

    /// <summary>
    /// For a writer that cannot resume from its hint: walks the live chain from genesis or, once files are archived,
    /// from the archive's newest record. Refuses to append when the live records do not continue the archive, rather
    /// than chain a new record onto a gap or start a second chain at genesis.
    /// </summary>
    private async Task<(SharedHead Head, long NextSequence)> WalkFromStartAsync(
        IReadOnlyList<string> segments, CancellationToken cancellationToken)
    {
        if (_archive is null)
            return await WalkAsync(segments, 0, 0, ScanState.Genesis, cancellationToken).ConfigureAwait(false);

        var archived = ListArchive(probe: true);
        var tail = await ArchiveTailAsync(archived, cancellationToken).ConfigureAwait(false);
        var live = AfterArchive(segments, archived);

        // A clock far enough behind would start a file the archive has already passed, which nothing would read.
        if (archived.Count > 0 && live.Count == 0 && _segmentOrder(_currentSegment(), archived[^1]) <= 0)
        {
            throw new IOException(
                $"This writer's current file {Path.GetFileName(_currentSegment())} for {_displayLocation} sorts at or before " +
                $"the archive's newest file {Path.GetFileName(archived[^1])}: its clock is behind. Not appending.");
        }

        var walked = await WalkAsync(live, 0, 0, tail, cancellationToken).ConfigureAwait(false);
        if (archived.Count > 0 && walked.Head.Sequence == tail.LastValidSequence && walked.NextSequence != tail.ExpectedSequence)
        {
            throw new IOException(
                $"The live audit records at {_displayLocation} do not continue the archive's newest record " +
                $"(sequence {tail.LastValidSequence}); not appending onto a gap. Verification reports where it breaks.");
        }

        return walked;
    }

    /// <summary>
    /// Checks that <paramref name="lines"/> continue a chain whose newest record is <paramref name="headSequence"/>
    /// (<c>-1</c> for an empty chain): every record carries the next sequence, points back to the one before and
    /// hashes to what it claims. What the archiver checks before it archives or witnesses a file.
    /// </summary>
    /// <param name="lines">The file's lines, in order. Blank lines are ignored.</param>
    /// <param name="headSequence">The newest record already in the chain, or <c>-1</c>.</param>
    /// <param name="headHash">Its hash, or the genesis hash.</param>
    /// <param name="allowPreChainLines">Whether lines written before the chain began may come first.</param>
    /// <param name="stepOverBreaks">
    /// For a file an operator accepted: step over lines that are not whole, self-consistent records (a line torn by a
    /// crash) instead of failing. A whole record that does not chain still fails.
    /// </param>
    /// <param name="witnessed">Hashes the archiver witnessed, by sequence: a record at one of them must have that hash.</param>
    /// <param name="newHead">
    /// The newest chaining record once the lines are added; on failure, the newest one verified before it (before the
    /// contradicting record, when a witness was contradicted).
    /// </param>
    /// <param name="reason">Why they do not continue the chain, when they do not.</param>
    /// <returns>Whether the lines continue the chain.</returns>
    internal static bool ContinuesChain(
        IEnumerable<string> lines,
        long headSequence,
        string headHash,
        bool allowPreChainLines,
        bool stepOverBreaks,
        IReadOnlyDictionary<long, string>? witnessed,
        out (long Sequence, string Hash) newHead,
        out string? reason)
    {
        var state = ScanState.FromHead(headSequence, headHash);
        state.ChainStarted |= !allowPreChainLines;
        foreach (var line in lines)
        {
            state.LineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var before = (state.LastValidSequence, state.LastValidHash);
            if (!ApplyLine(line, ref state, out var broken))
            {
                // Only what a crash leaves: a line that is not a whole record. A whole record that hashes right but does
                // not chain is never stepped over, or it could become the archive's newest record and outrank witnesses.
                if (stepOverBreaks && !IsSelfConsistentRecord(line))
                    continue;

                newHead = (state.LastValidSequence, state.LastValidHash);
                reason = broken.BreakReason;
                return false;
            }

            if (witnessed is not null
                && witnessed.TryGetValue(state.LastValidSequence, out var expected)
                && !string.Equals(expected, state.LastValidHash, StringComparison.Ordinal))
            {
                // Not the contradicting record: nothing from it on, or before it, is proven to be what was witnessed.
                newHead = before;
                reason = $"Record at sequence {state.LastValidSequence} differs from the one the archiver witnessed there " +
                         "(the log was rewritten after it was witnessed).";
                return false;
            }
        }

        newHead = (state.LastValidSequence, state.LastValidHash);
        reason = null;
        return true;
    }

    /// <summary>
    /// The newest record in <paramref name="lines"/> whose hash is right for its own content, or <c>null</c> when none
    /// is. Not simply the last line: an accepted file can end with a line torn by a crash.
    /// </summary>
    /// <param name="lines">A file's lines.</param>
    /// <returns>The record's sequence and hash.</returns>
    internal static (long Sequence, string Hash)? LastSelfConsistentRecord(IEnumerable<string> lines)
    {
        (long, string)? last = null;
        foreach (var line in lines)
        {
            if (TryParseLine(line, out var sequence, out var recordHash, out _, out _) && IsSelfConsistentRecord(line))
                last = (sequence, recordHash);
        }

        return last;
    }

    /// <summary>
    /// Whether <paramref name="line"/> is a whole record whose hash is right for its own content: what readers serve
    /// once a chain has begun, and the only kind of line an accepted file may not step over.
    /// </summary>
    /// <param name="line">A line.</param>
    /// <returns>Whether it is such a record.</returns>
    internal static bool IsSelfConsistentRecord(string line) =>
        TryParseLine(line, out var sequence, out var recordHash, out var previousHash, out var payloadJson)
        && string.Equals(ComputeRecordHash(sequence, previousHash, payloadJson), recordHash, StringComparison.Ordinal);

    /// <summary>The sequence and previous hash of the first well-formed record in <paramref name="lines"/>, or <c>null</c>.</summary>
    /// <param name="lines">A file's lines.</param>
    /// <returns>The record's sequence and the hash it points back to.</returns>
    internal static (long Sequence, string PreviousHash)? FirstRecord(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (TryParseLine(line, out var sequence, out _, out var previousHash, out _))
                return (sequence, previousHash);
        }

        return null;
    }

    /// <summary>The newest self-consistent record of an archived file read through the mount (see <see cref="LastSelfConsistentRecord"/>).</summary>
    private static async Task<(long Sequence, string Hash)?> LastRecordAsync(string path, CancellationToken cancellationToken) =>
        LastSelfConsistentRecord(await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false));

    /// <summary>The sequence and previous hash of a file's first well-formed record, or <c>null</c> when it has none.</summary>
    private static async Task<(long Sequence, string PreviousHash)?> FirstRecordAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (TryParseLine(line, out var sequence, out _, out var previousHash, out _))
                return (sequence, previousHash);
        }

        return null;
    }

    /// <summary>Whether the archived copy holds exactly the bytes the live file held when measured.</summary>
    private static async Task<bool> SameBytesAsync(SegmentReach live, string copy, CancellationToken cancellationToken)
    {
        await using var archived = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var current = new FileStream(live.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (archived.Length != live.Length)
            return false;

        var a = new byte[64 * 1024];
        var b = new byte[64 * 1024];
        for (long remaining = archived.Length; remaining > 0;)
        {
            var count = (int)Math.Min(a.Length, remaining);
            await archived.ReadExactlyAsync(a.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            await current.ReadExactlyAsync(b.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (!a.AsSpan(0, count).SequenceEqual(b.AsSpan(0, count)))
                return false;
            remaining -= count;
        }

        return true;
    }

    /// <summary>Runs an archive read, turning a failure to read it into "not verified" rather than a tamper break.</summary>
    private async Task<T> ArchiveReadAsync<T>(Func<Task<T>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is (IOException and not AuditFolderRefusedException) or UnauthorizedAccessException)
        {
            throw ArchiveUnreadable(ex);
        }
    }

    private AuditFolderRefusedException ArchiveUnreadable(Exception ex) => new(
        $"The audit archive at {_archiveRoot} could not be read ({ex.Message}). The chain at {_displayLocation} is not " +
        "written or verified until it can, so that archived records are not taken for deleted ones.");
}

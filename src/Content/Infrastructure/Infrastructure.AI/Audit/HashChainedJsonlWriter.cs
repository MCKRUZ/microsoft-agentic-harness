using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Domain.AI.Audit;
using Domain.Common;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Append-only JSONL writer that links every record into a cryptographic hash-chain,
/// making the log tamper-evident: any retroactive edit, deletion, or reordering of a
/// record breaks the chain at the first affected record and is detected by
/// <see cref="VerifyChainAsync(CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the shared integrity primitive for the harness's JSONL audit sinks. A sink
/// serializes its own typed record to JSON and hands the string here; this writer owns the
/// framing, the chain links, and thread-safe file access. Each persisted line has the form
/// <c>{json}\t{recordHash}\t{previousHash}\t{sequence}</c>, where
/// <c>recordHash = SHA-256(sequence + "\n" + previousHash + "\n" + json)</c>. Binding the
/// sequence number and the previous record's hash into each hash means a record cannot be
/// altered, removed, or moved without invalidating every record that follows it.
/// </para>
/// <para>
/// <b>Single-file and segmented modes.</b> A chain can back onto one file or span an ordered
/// set of <i>segments</i> (e.g. date-partitioned files). In segmented mode the sequence is
/// global across all segments and a record in a later segment chains onto the last record of
/// the previous one, so deleting an entire middle segment file breaks the chain exactly as
/// deleting a single record would. Segments must be appended in non-decreasing order (the
/// caller's segment selector should pick by append time, not by back-dated record time) so
/// sequence numbers stay monotonic across files.
/// </para>
/// <para>
/// <b>Framing safety.</b> The framing is line- and tab-delimited. <c>System.Text.Json</c>
/// escapes control characters inside string values (tab/newline become <c>\t</c>/<c>\n</c>),
/// so a serialized JSON payload never contains a raw framing byte. The writer rejects any
/// payload that does contain a raw tab, carriage-return, or newline rather than corrupt the
/// chain by splitting one logical record across physical lines.
/// </para>
/// <para>
/// <b>Choosing the head.</b> An append chains onto the last record that links to the chain
/// before it, found by walking the chain and stepping over any line that does not link. A
/// forged or torn line appended out of band therefore cannot seed the head; legitimate records
/// continue past it, and the forged/torn line is still surfaced by
/// <see cref="VerifyChainAsync(CancellationToken)"/>. The hashes are not keyed, so a forged
/// line that <i>does</i> link is indistinguishable from a real record; tamper-evidence covers
/// changes to records already written, not appends by someone who can write the folder.
/// </para>
/// <para>
/// <b>Brownfield rollout.</b> When pointed at files that already contain un-chained lines
/// written before this primitive was introduced, those legacy lines are treated as predating
/// the chain: the scan skips them, the chain genesis is the first chained record written after
/// rollout, and verification ignores the leading legacy lines. No silent rewrite of existing
/// history occurs.
/// </para>
/// <para>
/// <b>Several processes, one chain (#416).</b> Every append and every verification takes a lock file
/// beside the chain, held across processes, and the lock file also records where the last append left
/// the chain, as a hint the next append checks before resuming from it (see
/// <c>HashChainedJsonlWriter.SharedHead.cs</c>). So scaled-out copies of the host sharing one storage
/// location write one valid chain instead of interleaved ones that verify as tampering. Verification holds
/// the lock only long enough to note how far each segment reaches, then reads up to there, so a line
/// another process is half-way through writing is never read. A writer that waits more than ten seconds
/// for the lock gives up: the append fails and the verification throws <see cref="AuditChainBusyException"/>,
/// neither of which is reported as tampering.
/// </para>
/// <para>
/// <b>Where the lock holds.</b> It is the operating system's file lock: a share-mode lock on Windows,
/// <c>flock</c> on Linux and macOS. It holds on local disks, and on SMB shares (Azure Files) from Windows
/// or from a Linux kernel 5.5 or newer mounted without <c>nobrl</c>. It is not reliable over NFS, and
/// Windows and Linux hosts writing one chain on one share together has not been verified. On Linux and
/// macOS .NET ignores <c>flock</c> failures other than "already locked", and the
/// <c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c> switch turns the lock off entirely; either leaves writers
/// unserialized, which verification then reports as a broken chain. Roll an upgrade out to every copy
/// before they share a chain: a copy from before #416 takes no lock.
/// </para>
/// <para>
/// <b>Out of scope (handled by the verification layer).</b> A torn trailing line from a crash
/// mid-write, or wholesale deletion of the file/last segment, are not concealable by this
/// primitive but are not <i>repaired</i> by it either — those are the province of WORM storage
/// and the scheduled chain-verification job. This primitive guarantees detection, not crash-safe
/// truncation.
/// </para>
/// <para>
/// <b>Cost note.</b> An append checks the hint against one record and reads only what was written
/// since, so its cost does not grow with the chain. Without a usable hint (a new deployment, files
/// written before #416, or a hint that no longer matches) it walks the whole chain once. Verification
/// scans it in full. Both are O(records).
/// </para>
/// </remarks>
public sealed partial class HashChainedJsonlWriter : IDisposable
{
    /// <summary>The 64-character all-zero hash that precedes the first record in a chain.</summary>
    public const string GenesisHash =
        "0000000000000000000000000000000000000000000000000000000000000000";

    private const char FieldSeparator = '\t';

    /// <summary>Characters that would break the line/tab framing if present in a payload.</summary>
    private static readonly SearchValues<char> FramingChars = SearchValues.Create("\t\n\r");

    private readonly Func<string> _currentSegment;
    private readonly Func<IReadOnlyList<string>> _orderedSegments;
    private readonly string _displayLocation;
    private readonly string _lockPath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Initializes a single-file chain backed by one JSONL file.</summary>
    /// <param name="filePath">Absolute path to the JSONL file backing this chain.</param>
    /// <param name="logger">Logger for operational diagnostics.</param>
    public HashChainedJsonlWriter(string filePath, ILogger logger)
        : this(
            filePath,
            () => filePath,
            () => File.Exists(filePath) ? [filePath] : [],
            filePath + ".lock",
            logger)
    {
    }

    /// <summary>Initializes a segmented chain spanning an ordered set of JSONL files.</summary>
    /// <param name="displayLocation">A directory or label used in diagnostic log messages.</param>
    /// <param name="currentSegment">Selects the segment file the next record is appended to (by append time).</param>
    /// <param name="orderedSegments">Returns all existing segment files in chain (append) order.</param>
    /// <param name="lockPath">
    /// The file every process writing this chain locks while appending. Must be the same path for every
    /// writer of the chain and must not be one of its segments.
    /// </param>
    /// <param name="logger">Logger for operational diagnostics.</param>
    public HashChainedJsonlWriter(
        string displayLocation,
        Func<string> currentSegment,
        Func<IReadOnlyList<string>> orderedSegments,
        string lockPath,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayLocation);
        ArgumentNullException.ThrowIfNull(currentSegment);
        ArgumentNullException.ThrowIfNull(orderedSegments);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        ArgumentNullException.ThrowIfNull(logger);

        _displayLocation = displayLocation;
        _currentSegment = currentSegment;
        _orderedSegments = orderedSegments;
        _lockPath = lockPath;
        _logger = logger;
    }

    /// <summary>
    /// Extracts the original payload JSON from a persisted chain line (the text before the first
    /// framing tab). Tolerates legacy un-chained lines, which are returned unchanged.
    /// </summary>
    /// <param name="line">A persisted line.</param>
    /// <returns>The payload JSON portion of the line.</returns>
    public static string ExtractPayload(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var tabIndex = line.IndexOf(FieldSeparator);
        return tabIndex < 0 ? line : line[..tabIndex];
    }

    /// <summary>
    /// Reads every payload in the chain, across all segments in append order, without verifying
    /// hash links — use <see cref="VerifyChainAsync(CancellationToken)"/> for integrity checking. Blank lines are
    /// skipped. Does not take the append semaphore (matching the two hand-written readers this
    /// primitive replaces), so a concurrent append may or may not be visible in a given pass.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The payload JSON of every persisted line, in append order.</returns>
    public async IAsyncEnumerable<string> ReadAllPayloadsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var segmentPath in _orderedSegments())
        {
            if (!File.Exists(segmentPath))
                continue;

            await using var stream = new FileStream(
                segmentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                yield return ExtractPayload(line);
            }
        }
    }

    /// <summary>
    /// Appends a serialized record to the chain, computing and persisting its hash links.
    /// </summary>
    /// <param name="payloadJson">The caller's already-serialized record as a single-line JSON string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when the record was persisted; a failure result when the
    /// payload contains a raw framing character or the file could not be written.
    /// </returns>
    public async Task<Result> AppendAsync(string payloadJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);

        if (payloadJson.AsSpan().IndexOfAny(FramingChars) >= 0)
            return Result.Fail(
                "Audit payload must not contain raw tab, carriage-return, or newline characters; chain framing is line- and tab-delimited.");

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Across processes: no other writer appends between reading the head and recording the new one.
            await using var chainLock = await AcquireChainLockAsync(cancellationToken).ConfigureAwait(false);
            var segments = WithCurrentSegment(_orderedSegments());
            var (head, sequence) = await LoadHeadAsync(chainLock, segments, cancellationToken).ConfigureAwait(false);

            var previousHash = head.Hash;
            var recordHash = ComputeRecordHash(sequence, previousHash, payloadJson);
            var line = string.Concat(
                payloadJson, FieldSeparator,
                recordHash, FieldSeparator,
                previousHash, FieldSeparator,
                sequence.ToString(CultureInfo.InvariantCulture), "\n");

            var segmentPath = ChooseTargetSegment(segments);
            // Owner-only (#640, following #527's precedent): this is the tamper-evident audit log
            // covering governance/escalation/drift/change/egress events -- the most security-relevant
            // of the stores #640 migrated, since its tamper-evidence hash chain protects integrity but
            // says nothing about confidentiality on a shared host.
            OwnerOnlyDirectoryHelper.Create(Path.GetDirectoryName(segmentPath)!, _logger);

            var lengthAfterWrite = AppendLine(segmentPath, line);
            WriteHint(chainLock, new SharedHead(Path.GetFileName(segmentPath), lengthAfterWrite, sequence, recordHash));
            return Result.Success();
        }
        catch (AuditChainBusyException ex)
        {
            _logger.LogError(ex, "Audit record not written to {AuditLocation}: the chain is busy", _displayLocation);
            return Result.Fail("Failed to persist audit record: the audit chain is busy. Retry the operation.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to append audit record to {AuditLocation}", _displayLocation);
            return Result.Fail($"Failed to persist audit record: {ex.Message}");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Walks the chain from genesis across all segments and recomputes every link, reporting
    /// whether the log is intact and, if not, the first record where it broke.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A verification result describing the chain's integrity.</returns>
    public Task<AuditChainVerificationResult> VerifyChainAsync(CancellationToken cancellationToken) =>
        VerifyChainAsync(checkpoint: null, cancellationToken);

    /// <summary>
    /// Verifies the chain as <see cref="VerifyChainAsync(CancellationToken)"/> does and, when a
    /// <paramref name="checkpoint"/> is given, also requires the chain to still reach it with the same
    /// hash there (#416).
    /// </summary>
    /// <param name="checkpoint">The point the chain was last verified up to, or <c>null</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A verification result describing the chain's integrity, with its head.</returns>
    public async Task<AuditChainVerificationResult> VerifyChainAsync(
        AuditChainCheckpoint? checkpoint, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Note how far each segment reaches while no other process is mid-append, then read only that
            // far, so a half-written line from another writer is never mistaken for tampering.
            IReadOnlyList<SegmentReach> reach;
            await using (await AcquireChainLockAsync(cancellationToken).ConfigureAwait(false))
            {
                // Lengths through open handles, not cached directory entries, so another host's appends are seen.
                reach = _orderedSegments()
                    .Select(p => new SegmentReach(p, LengthOf(p)))
                    .Where(s => s.Length is not null)
                    .ToList();
            }

            var scan = await ScanChainAsync(checkpoint, cancellationToken, reach).ConfigureAwait(false);
            return scan.BreakSequence is { } brokenSequence
                ? AuditChainVerificationResult.Broken(scan.VerifiedCount, brokenSequence, scan.BreakReason!) with
                {
                    HeadSequence = scan.LastValidSequence,
                    HeadHash = scan.LastValidSequence >= 0 ? scan.LastValidHash : null
                }
                : AuditChainVerificationResult.Valid(
                    scan.VerifiedCount,
                    scan.LastValidSequence,
                    scan.LastValidSequence >= 0 ? scan.LastValidHash : null);
        }
        // A busy chain is not a broken one: let it propagate rather than report a tamper break at 0.
        catch (Exception ex) when (ex is (IOException and not AuditChainBusyException) or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to read audit chain at {AuditLocation}", _displayLocation);
            return AuditChainVerificationResult.Broken(0, 0, $"Failed to read audit chain: {ex.Message}");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose() => _semaphore.Dispose();

    /// <summary>
    /// Single forward pass over all segments shared by verification and head recovery. Validates
    /// sequence continuity, previous-hash links, and record hashes from genesis, tolerating only
    /// the leading legacy (pre-chain) lines. With <paramref name="reach"/>, reads exactly those
    /// segments up to those lengths, and a segment that has gone missing since is a break.
    /// </summary>
    private async Task<ChainScan> ScanChainAsync(
        AuditChainCheckpoint? checkpoint,
        CancellationToken cancellationToken,
        IReadOnlyList<SegmentReach>? reach = null)
    {
        var state = ScanState.Genesis;

        foreach (var segment in reach ?? _orderedSegments().Select(p => new SegmentReach(p, null)).ToList())
        {
            if (!File.Exists(segment.Path))
            {
                if (reach is not null)
                    return state.Break(state.ExpectedSequence, $"Segment {Path.GetFileName(segment.Path)} disappeared during verification.");
                continue;
            }

            await using var stream = new FileStream(
                segment.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(
                segment.Length is { } length ? new PrefixStream(stream, length) : stream);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                state.LineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!ApplyLine(line, ref state, out var breakScan))
                    return breakScan;

                // The record the last pass ended on must still be there, unchanged. A different hash at
                // that sequence means the chain was regrown from somewhere earlier, not appended to.
                if (checkpoint is not null
                    && state.LastValidSequence == checkpoint.Sequence
                    && !string.Equals(state.LastValidHash, checkpoint.Hash, StringComparison.Ordinal))
                {
                    return state.Break(checkpoint.Sequence,
                        $"Record at sequence {checkpoint.Sequence} differs from the one last verified there (chain rewritten or regrown).");
                }
            }
        }

        // A clean chain that stops before the last verified record was cut off. Internal consistency
        // cannot show this; only the checkpoint can.
        if (checkpoint is not null && state.LastValidSequence < checkpoint.Sequence)
        {
            return state.Break(state.LastValidSequence + 1,
                $"Chain ends at sequence {state.LastValidSequence} but was last verified up to {checkpoint.Sequence} (records removed from the end, or the log was deleted).");
        }

        return ChainScan.Valid(state.VerifiedCount, state.LastValidSequence, state.LastValidHash);
    }

    /// <summary>
    /// Validates one line against the running chain state. Returns false and yields a break
    /// result when the line is tampered/out-of-place; otherwise advances the state and returns true.
    /// </summary>
    private static bool ApplyLine(string line, ref ScanState state, out ChainScan breakScan)
    {
        breakScan = default;

        if (!TryParseLine(line, out var sequence, out var recordHash, out var previousHash, out var payloadJson))
        {
            // Legacy lines that predate the chain are tolerated only before it starts.
            if (state.ChainStarted)
            {
                breakScan = state.Break(state.ExpectedSequence, $"Malformed chain line at line {state.LineNumber}.");
                return false;
            }
            return true;
        }

        state.ChainStarted = true;
        if (sequence > state.HighestSequenceSeen)
            state.HighestSequenceSeen = sequence;

        if (sequence != state.ExpectedSequence)
        {
            breakScan = state.Break(state.ExpectedSequence,
                $"Sequence gap: expected {state.ExpectedSequence} but found {sequence} at line {state.LineNumber} (record deleted or reordered).");
            return false;
        }

        if (!string.Equals(previousHash, state.ExpectedPreviousHash, StringComparison.Ordinal))
        {
            breakScan = state.Break(sequence, $"Previous-hash mismatch at sequence {sequence} (chain link altered).");
            return false;
        }

        var computedHash = ComputeRecordHash(sequence, previousHash, payloadJson);
        if (!string.Equals(computedHash, recordHash, StringComparison.Ordinal))
        {
            breakScan = state.Break(sequence, $"Record-hash mismatch at sequence {sequence} (record content altered).");
            return false;
        }

        state.Advance(sequence, recordHash);
        return true;
    }

    private static string ComputeRecordHash(long sequence, string previousHash, string payloadJson)
    {
        var material = string.Concat(
            sequence.ToString(CultureInfo.InvariantCulture), "\n", previousHash, "\n", payloadJson);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static bool TryParseLine(
        string line, out long sequence, out string recordHash, out string previousHash, out string payloadJson)
    {
        sequence = -1;
        recordHash = string.Empty;
        previousHash = string.Empty;
        payloadJson = string.Empty;

        var parts = line.Split(FieldSeparator);
        if (parts.Length != 4)
            return false;

        // The writer only ever emits a plain invariant-culture integer, so reject anything
        // looser (leading sign, surrounding whitespace) to shrink the false-positive surface.
        if (!long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out sequence))
            return false;

        payloadJson = parts[0];
        recordHash = parts[1];
        previousHash = parts[2];
        return true;
    }

    /// <summary>Mutable running state for a single forward pass over the chain.</summary>
    private struct ScanState
    {
        public long ExpectedSequence;
        public string ExpectedPreviousHash;
        public long VerifiedCount;
        public long LastValidSequence;
        public string LastValidHash;
        public bool ChainStarted;
        public int LineNumber;

        /// <summary>
        /// The highest sequence on any well-formed line, chaining or not. A writer never assigns a sequence at
        /// or below it, so a record stepped over (after a deleted segment, say) is never given a twin.
        /// </summary>
        public long HighestSequenceSeen;

        /// <summary>State that continues an already-validated chain whose newest record is <paramref name="headSequence"/>.</summary>
        public static ScanState FromHead(long headSequence, string headHash) => headSequence < 0
            ? Genesis
            : new()
            {
                ExpectedSequence = headSequence + 1,
                ExpectedPreviousHash = headHash,
                VerifiedCount = 0,
                LastValidSequence = headSequence,
                LastValidHash = headHash,
                ChainStarted = true,
                LineNumber = 0,
                HighestSequenceSeen = headSequence
            };

        public static ScanState Genesis => new()
        {
            ExpectedSequence = 0,
            ExpectedPreviousHash = GenesisHash,
            VerifiedCount = 0,
            LastValidSequence = -1,
            LastValidHash = GenesisHash,
            ChainStarted = false,
            LineNumber = 0,
            HighestSequenceSeen = -1
        };

        /// <summary>
        /// The sequence for the next record: one past the newest chaining record, or one past the highest seen
        /// when that is higher. A sequence too large to step past is ignored rather than wrapped.
        /// </summary>
        public readonly long NextSequence =>
            HighestSequenceSeen > LastValidSequence && HighestSequenceSeen < long.MaxValue
                ? HighestSequenceSeen + 1
                : LastValidSequence + 1;

        public void Advance(long sequence, string recordHash)
        {
            VerifiedCount++;
            LastValidSequence = sequence;
            LastValidHash = recordHash;
            ExpectedSequence = sequence + 1;
            ExpectedPreviousHash = recordHash;
        }

        public readonly ChainScan Break(long breakSequence, string reason) =>
            ChainScan.Break(VerifiedCount, breakSequence, LastValidSequence, LastValidHash, reason);
    }

    /// <summary>
    /// A read-only view of the first <c>length</c> bytes from a stream's current position, so a reader stops
    /// where the chain reached when it was measured instead of reading into a line still being written.
    /// </summary>
    private sealed class PrefixStream(Stream inner, long length) : Stream
    {
        private long _remaining = Math.Max(0, length);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
                return 0;

            var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
                return 0;

            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A segment to scan and, when set, how many bytes of it to read.</summary>
    private readonly record struct SegmentReach(string Path, long? Length);

    /// <summary>Outcome of a single forward pass over the chain.</summary>
    /// <param name="VerifiedCount">Records that verified cleanly from genesis before any break.</param>
    /// <param name="LastValidSequence">Sequence of the last valid record, or -1 if none.</param>
    /// <param name="LastValidHash">Record hash of the last valid record, or the genesis hash if none.</param>
    /// <param name="BreakSequence">Sequence where the chain broke, or null when intact.</param>
    /// <param name="BreakReason">Explanation of the break, or null when intact.</param>
    private readonly record struct ChainScan(
        long VerifiedCount,
        long LastValidSequence,
        string LastValidHash,
        long? BreakSequence,
        string? BreakReason)
    {
        public static ChainScan Valid(long verifiedCount, long lastValidSequence, string lastValidHash) =>
            new(verifiedCount, lastValidSequence, lastValidHash, null, null);

        public static ChainScan Break(
            long verifiedCount, long breakSequence, long lastValidSequence, string lastValidHash, string reason) =>
            new(verifiedCount, lastValidSequence, lastValidHash, breakSequence, reason);
    }
}

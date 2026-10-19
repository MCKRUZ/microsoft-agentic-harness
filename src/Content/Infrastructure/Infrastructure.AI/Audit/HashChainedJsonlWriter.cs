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
/// <b>Trusted head recovery.</b> On first append after a restart the writer rebuilds its
/// in-memory head by scanning the segments and validating the chain — the head is set to the
/// last <i>cryptographically valid</i> record, never to an unverified tail. A forged or torn
/// line appended out of band therefore cannot seed the live head; legitimate records continue
/// to chain onto verified state, and the forged/torn line is still surfaced by
/// <see cref="VerifyChainAsync(CancellationToken)"/>.
/// </para>
/// <para>
/// <b>Brownfield rollout.</b> When pointed at files that already contain un-chained lines
/// written before this primitive was introduced, those legacy lines are treated as predating
/// the chain: the scan skips them, the chain genesis is the first chained record written after
/// rollout, and verification ignores the leading legacy lines. No silent rewrite of existing
/// history occurs.
/// </para>
/// <para>
/// <b>Several processes, one chain (#416).</b> Every append takes a lock file beside the chain, held across
/// processes, and before writing catches up with any records another process appended since this one last
/// wrote, validating them link by link from its own head. So scaled-out copies of the host sharing one
/// storage location write one valid chain instead of two interleaved ones that verify as tampering. The
/// lock is the operating system's file lock: share modes on Windows, <c>flock</c> on Linux and macOS. It
/// holds on local disks and on SMB shares (Azure Files) from Windows or a Linux kernel newer than 5.4; it
/// does not hold over NFS. Verification takes the lock only long enough to note how far each segment
/// reaches, then reads up to there, so a line another process is half-way through writing is never read.
/// </para>
/// <para>
/// <b>Out of scope (handled by the verification layer).</b> A torn trailing line from a crash
/// mid-write, or wholesale deletion of the file/last segment, are not concealable by this
/// primitive but are not <i>repaired</i> by it either — those are the province of WORM storage
/// and the scheduled chain-verification job. This primitive guarantees detection, not crash-safe
/// truncation.
/// </para>
/// <para>
/// <b>Cost note.</b> Head recovery scans the chain once per process lifetime (on first append);
/// verification scans it in full. Both are O(records). For the append-only audit volumes this
/// primitive targets, that cost is paid rarely and is acceptable.
/// </para>
/// </remarks>
public sealed class HashChainedJsonlWriter : IDisposable
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

    private bool _headInitialized;
    private long _headSequence = -1;
    private string _headHash = GenesisHash;

    /// <summary>The segment and length this process has validated the chain through.</summary>
    private string? _tailSegment;
    private long _tailLength;

    /// <summary>How long an append waits for another process to release the chain's lock.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait between attempts to take the lock.</summary>
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(15);

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
            // Across processes: no other writer appends while this one catches up and writes.
            await using var chainLock = await AcquireChainLockAsync(cancellationToken).ConfigureAwait(false);
            await CatchUpWithOtherWritersAsync(cancellationToken).ConfigureAwait(false);

            var sequence = _headSequence + 1;
            var previousHash = _headHash;
            var recordHash = ComputeRecordHash(sequence, previousHash, payloadJson);
            var line = string.Concat(
                payloadJson, FieldSeparator,
                recordHash, FieldSeparator,
                previousHash, FieldSeparator,
                sequence.ToString(CultureInfo.InvariantCulture), "\n");

            var segmentPath = _currentSegment();
            // Owner-only (#640, following #527's precedent): this is the tamper-evident audit log
            // covering governance/escalation/drift/change/egress events -- the most security-relevant
            // of the stores #640 migrated, since its tamper-evidence hash chain protects integrity but
            // says nothing about confidentiality on a shared host.
            OwnerOnlyDirectoryHelper.Create(Path.GetDirectoryName(segmentPath)!, _logger);

            long lengthAfterWrite;
            await using (var stream = new FileStream(
                segmentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                await using (var writer = new StreamWriter(stream, leaveOpen: true))
                {
                    await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                lengthAfterWrite = stream.Length;
            }

            _headSequence = sequence;
            _headHash = recordHash;
            _tailSegment = segmentPath;
            _tailLength = lengthAfterWrite;
            return Result.Success();
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
            IReadOnlyDictionary<string, long> reach;
            await using (await AcquireChainLockAsync(cancellationToken).ConfigureAwait(false))
            {
                reach = _orderedSegments()
                    .Where(File.Exists)
                    .ToDictionary(p => p, p => new FileInfo(p).Length, StringComparer.Ordinal);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
    /// Brings this process's head up to date with the chain on disk, under the chain lock. The first time,
    /// and whenever the chain changed in a way it cannot follow (another writer started a new segment, or
    /// the file shrank), it scans and validates every segment and takes the last valid record as the head.
    /// Otherwise it validates only what other writers appended since this process last wrote, link by link
    /// from its own head, so a busy shared chain does not cost a full scan per append. Either way the head
    /// is always cryptographically verified state, never an unverified tail.
    /// </summary>
    private async Task CatchUpWithOtherWritersAsync(CancellationToken cancellationToken)
    {
        var segments = _orderedSegments();
        var last = segments.Count > 0 ? segments[^1] : null;
        var lastLength = last is not null && File.Exists(last) ? new FileInfo(last).Length : 0;

        if (_headInitialized && last == _tailSegment && lastLength == _tailLength)
            return;

        if (_headInitialized && last is not null && last == _tailSegment && lastLength > _tailLength)
        {
            await ValidateAppendedTailAsync(last, lastLength, cancellationToken).ConfigureAwait(false);
            return;
        }

        var scan = await ScanChainAsync(checkpoint: null, cancellationToken).ConfigureAwait(false);
        _headSequence = scan.LastValidSequence;
        _headHash = scan.LastValidHash;
        _headInitialized = true;
        _tailSegment = last;
        _tailLength = lastLength;
    }

    /// <summary>
    /// Validates the records other writers appended to <paramref name="segment"/> after
    /// <see cref="_tailLength"/>, continuing from this process's head. A line that does not chain is left
    /// in place (verification reports it) and the head stays at the last valid record.
    /// </summary>
    private async Task ValidateAppendedTailAsync(string segment, long length, CancellationToken cancellationToken)
    {
        var state = ScanState.FromHead(_headSequence, _headHash);

        await using (var stream = new FileStream(segment, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            stream.Seek(_tailLength, SeekOrigin.Begin);
            using var reader = new StreamReader(new PrefixStream(stream, length - _tailLength));

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                state.LineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!ApplyLine(line, ref state, out var breakScan))
                {
                    _logger.LogError(
                        "Audit chain at {AuditLocation}: a record appended by another writer does not chain ({Reason}); " +
                        "continuing from the last valid record, and verification will report the break.",
                        _displayLocation, breakScan.BreakReason);
                    break;
                }
            }
        }

        _headSequence = state.LastValidSequence;
        _headHash = state.LastValidHash;
        _tailLength = length;
    }

    /// <summary>
    /// Takes the chain's cross-process lock, retrying while another process holds it. The lock is an
    /// exclusively opened lock file (a share-mode lock on Windows, <c>flock</c> elsewhere) and is released
    /// by disposing the returned handle. The file itself is left in place: deleting it would race another
    /// process that is about to open it.
    /// </summary>
    private async Task<FileStream> AcquireChainLockAsync(CancellationToken cancellationToken)
    {
        OwnerOnlyDirectoryHelper.Create(Path.GetDirectoryName(Path.GetFullPath(_lockPath))!, _logger);

        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(LockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Single forward pass over all segments shared by verification and head recovery. Validates
    /// sequence continuity, previous-hash links, and record hashes from genesis, tolerating only
    /// the leading legacy (pre-chain) lines.
    /// </summary>
    private async Task<ChainScan> ScanChainAsync(
        AuditChainCheckpoint? checkpoint,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, long>? reach = null)
    {
        var state = ScanState.Genesis;

        foreach (var segmentPath in reach?.Keys ?? _orderedSegments())
        {
            if (!File.Exists(segmentPath))
                continue;

            await using var stream = new FileStream(
                segmentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(
                reach is not null ? new PrefixStream(stream, reach[segmentPath]) : stream);

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
                LineNumber = 0
            };

        public static ScanState Genesis => new()
        {
            ExpectedSequence = 0,
            ExpectedPreviousHash = GenesisHash,
            VerifiedCount = 0,
            LastValidSequence = -1,
            LastValidHash = GenesisHash,
            ChainStarted = false,
            LineNumber = 0
        };

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

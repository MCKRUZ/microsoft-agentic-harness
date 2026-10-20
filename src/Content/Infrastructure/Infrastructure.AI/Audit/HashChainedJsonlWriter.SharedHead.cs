using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>
/// The cross-process half of <see cref="HashChainedJsonlWriter"/> (#416): the lock every writer of a chain
/// takes, and the chain head every writer reads from and writes back to inside that lock.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One shared head, kept in the lock file.</strong> Each writer used to keep its own head in
/// memory and re-derive it by scanning the files. With several processes, that let two writers disagree:
/// a host whose clock put it in an earlier day's segment, a stale file size cached by an SMB client, or a
/// torn line left by a crashed writer each led two writers to the same sequence number. Now the head
/// (segment, length, sequence, hash) is written into the lock file by whoever appended last, and read
/// back from it, through the locked handle, by whoever appends next. Every writer starts from the same
/// state, so they cannot fork.
/// </para>
/// <para>
/// <strong>What a writer still checks.</strong> That the record the head names really ends the chain where
/// the head says (a cheap read of one line); that the chain did not get shorter than the head (a shrink is
/// never legitimate for an append-only log, so it is reported at Critical and the head is never lowered,
/// leaving the gap visible to verification); and, link by link, anything written past the head by a
/// writer that crashed before recording it. A line that does not chain is skipped, logged, and left for
/// verification to report, and every writer skips it identically. Only with no usable head (first run,
/// or after an upgrade) does a writer scan the whole chain; that is also what keeps a restart from paying
/// for a full scan on every first append.
/// </para>
/// <para>
/// <strong>Trust.</strong> The head in the lock file is not a security boundary: whoever can write the
/// chain's folder can write it, and could equally append records that chain (the hashes are not keyed).
/// A forged head makes later records chain onto the forgery, which verification reports; it cannot make
/// a tampered chain verify.
/// </para>
/// </remarks>
public sealed partial class HashChainedJsonlWriter
{
    /// <summary>How long an append or a verification waits for another process to release the chain's lock.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait between attempts to take the lock.</summary>
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(15);

    /// <summary>How far back from a recorded length to look for the record that ends there.</summary>
    private const int HeadCheckWindowBytes = 1024 * 1024;

    /// <summary>The chain's head as the last writer recorded it.</summary>
    /// <param name="Segment">The file name (not the path) of the segment the newest record is in.</param>
    /// <param name="Length">That segment's length just after the newest record was written.</param>
    /// <param name="Sequence">The newest record's sequence, or -1 for an empty chain.</param>
    /// <param name="Hash">The newest record's hash, or the genesis hash for an empty chain.</param>
    private sealed record SharedHead(string Segment, long Length, long Sequence, string Hash);

    /// <summary>
    /// Takes the chain's cross-process lock, retrying while another process holds it. Released by disposing
    /// the returned handle. The file is left in place: deleting it would race a process about to open it.
    /// </summary>
    /// <exception cref="AuditChainBusyException">Another process held the lock for longer than <see cref="LockTimeout"/>.</exception>
    private async Task<FileStream> AcquireChainLockAsync(CancellationToken cancellationToken)
    {
        OwnerOnlyDirectoryHelper.Create(Path.GetDirectoryName(Path.GetFullPath(_lockPath))!, _logger);

        // Elapsed time, not wall-clock time, so a clock step cannot shorten or stretch the wait.
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                if (waited.Elapsed >= LockTimeout)
                    throw new AuditChainBusyException(_displayLocation, LockTimeout, ex);

                await Task.Delay(LockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Works out the head to append after, inside the lock: the recorded one when it still matches the files,
    /// extended past anything a crashed writer left unrecorded; a full scan when there is no usable record.
    /// </summary>
    private async Task<SharedHead> LoadHeadAsync(FileStream lockFile, CancellationToken cancellationToken)
    {
        var segments = _orderedSegments();
        var recorded = await ReadRecordedHeadAsync(lockFile, cancellationToken).ConfigureAwait(false);

        if (recorded is not null && recorded.Sequence >= 0)
        {
            var recordedIndex = IndexOfSegment(segments, recorded.Segment);
            if (recordedIndex < 0)
            {
                _logger.LogCritical(
                    "Audit chain at {AuditLocation}: segment {Segment}, which holds its newest record, is missing. " +
                    "Continuing after the recorded head so verification reports the gap.",
                    _displayLocation, recorded.Segment);
                return recorded;
            }

            var length = new FileInfo(segments[recordedIndex]).Length;
            if (length < recorded.Length)
            {
                _logger.LogCritical(
                    "Audit chain at {AuditLocation}: segment {Segment} is {Length} bytes, shorter than the {Recorded} its " +
                    "newest record ended at. Continuing after the recorded head so verification reports the gap.",
                    _displayLocation, recorded.Segment, length, recorded.Length);
                return recorded;
            }

            if (await EndsWithRecordAsync(segments[recordedIndex], recorded, cancellationToken).ConfigureAwait(false))
                return await ExtendPastRecordedAsync(segments, recordedIndex, recorded, cancellationToken).ConfigureAwait(false);

            _logger.LogWarning(
                "Audit chain at {AuditLocation}: the recorded head does not match the record at its position; rescanning the chain.",
                _displayLocation);
        }

        var scan = await ScanChainAsync(checkpoint: null, cancellationToken).ConfigureAwait(false);
        var last = segments.Count > 0 ? segments[^1] : null;
        return new SharedHead(
            last is null ? string.Empty : Path.GetFileName(last),
            last is null || !File.Exists(last) ? 0 : new FileInfo(last).Length,
            scan.LastValidSequence,
            scan.LastValidHash);
    }

    /// <summary>
    /// Validates anything written after the recorded head (the rest of its segment, then any later segment)
    /// link by link from it. Only a writer that crashed after writing a record but before recording it, or
    /// a writer that takes no lock, leaves anything here. Lines that do not chain are skipped and logged;
    /// every writer skips the same ones, so they still agree on the head.
    /// </summary>
    private async Task<SharedHead> ExtendPastRecordedAsync(
        IReadOnlyList<string> segments, int recordedIndex, SharedHead recorded, CancellationToken cancellationToken)
    {
        var state = ScanState.FromHead(recorded.Sequence, recorded.Hash);
        var head = recorded;

        for (var i = recordedIndex; i < segments.Count; i++)
        {
            var path = segments[i];
            var start = i == recordedIndex ? recorded.Length : 0;
            var end = new FileInfo(path).Length;
            if (end <= start)
            {
                head = head with { Segment = Path.GetFileName(path), Length = Math.Max(end, start) };
                continue;
            }

            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(new PrefixStream(stream, end - start));
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    state.LineNumber++;
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    if (!ApplyLine(line, ref state, out var breakScan))
                    {
                        _logger.LogError(
                            "Audit chain at {AuditLocation}: skipping a line past the recorded head that does not chain ({Reason}); " +
                            "verification will report it.",
                            _displayLocation, breakScan.BreakReason);
                    }
                }
            }

            head = new SharedHead(Path.GetFileName(path), end, state.LastValidSequence, state.LastValidHash);
        }

        return head;
    }

    /// <summary>Whether the record the head names is the one that ends its segment at the recorded length.</summary>
    private static async Task<bool> EndsWithRecordAsync(string path, SharedHead head, CancellationToken cancellationToken)
    {
        if (head.Length <= 0)
            return false;

        var window = (int)Math.Min(head.Length, HeadCheckWindowBytes);
        var buffer = new byte[window];
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            stream.Seek(head.Length - window, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        var text = Encoding.UTF8.GetString(buffer).TrimEnd('\n');
        var lastLine = text[(text.LastIndexOf('\n') + 1)..];
        return TryParseLine(lastLine, out var sequence, out var recordHash, out _, out _)
            && sequence == head.Sequence
            && string.Equals(recordHash, head.Hash, StringComparison.Ordinal);
    }

    private static int IndexOfSegment(IReadOnlyList<string> segments, string fileName)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            if (string.Equals(Path.GetFileName(segments[i]), fileName, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// The segment to append to: the latest by name of the caller's current segment, the head's segment and
    /// the last existing one. Never an earlier one, so a host whose clock is behind cannot write into a
    /// segment the chain has already moved past, which would put sequence numbers out of order on disk.
    /// </summary>
    private string ChooseTargetSegment(IReadOnlyList<string> segments, SharedHead head)
    {
        var target = _currentSegment();
        var directory = Path.GetDirectoryName(target)!;

        foreach (var candidate in new[] { segments.Count > 0 ? segments[^1] : null, head.Segment.Length > 0 ? Path.Combine(directory, head.Segment) : null })
        {
            if (candidate is not null
                && string.CompareOrdinal(Path.GetFileName(candidate), Path.GetFileName(target)) > 0)
            {
                target = candidate;
            }
        }

        return target;
    }

    /// <summary>
    /// Appends <paramref name="line"/> to <paramref name="path"/> in one write, first ending any line a crashed
    /// writer left unterminated so the new record never glues onto it. Not cancellable once started: a record
    /// written halfway is exactly the torn line this guards against. Returns the segment's new length.
    /// </summary>
    private static long AppendLine(string path, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
                stream.WriteByte((byte)'\n');
        }

        stream.Seek(0, SeekOrigin.End);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return stream.Length;
    }

    private static async Task<SharedHead?> ReadRecordedHeadAsync(FileStream lockFile, CancellationToken cancellationToken)
    {
        if (lockFile.Length == 0)
            return null;

        try
        {
            lockFile.Seek(0, SeekOrigin.Begin);
            var head = await JsonSerializer.DeserializeAsync<SharedHead>(lockFile, cancellationToken: cancellationToken).ConfigureAwait(false);
            return head is { Hash.Length: > 0, Segment: not null } ? head : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteRecordedHead(FileStream lockFile, SharedHead head)
    {
        lockFile.Seek(0, SeekOrigin.Begin);
        lockFile.SetLength(0);
        JsonSerializer.Serialize(lockFile, head);
        lockFile.Flush(flushToDisk: true);
    }
}

/// <summary>
/// Another process held an audit chain's lock for longer than the writer waits. Not tampering, and not
/// reported as a broken chain: the chain is busy, and the operation can be retried.
/// </summary>
public sealed class AuditChainBusyException : IOException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="location">The chain's location, for the message.</param>
    /// <param name="waited">How long the writer waited.</param>
    /// <param name="inner">The last failure to take the lock.</param>
    public AuditChainBusyException(string location, TimeSpan waited, Exception inner)
        : base($"The audit chain at {location} is busy: another writer held its lock for more than {waited.TotalSeconds:0} seconds.", inner)
    {
    }
}

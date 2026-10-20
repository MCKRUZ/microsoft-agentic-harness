using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Domain.AI.Audit;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>
/// The cross-process half of <see cref="HashChainedJsonlWriter"/> (#416): the lock every writer of a chain
/// takes, and how a writer works out the head to append after while holding it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One rule decides the head.</strong> Every writer walks the chain the same way: it tolerates
/// legacy lines before the chain starts, skips (and logs) any later line that does not chain, and takes the
/// last record that does. Verification reports the skipped lines; the writers only step over them.
/// </para>
/// <para>
/// <strong>No sequence is handed out twice.</strong> A walk resumed from the hint and a walk from the start
/// agree on the head only while the chain has no break in the middle. Once it does (a deleted segment, an
/// edited record), the two can differ. So the next sequence is never just "head plus one": it is one past
/// the highest sequence on any well-formed line the walk passed, chaining or not. Whichever way a writer
/// walked, it cannot give a new record a sequence an existing line already carries.
/// </para>
/// <para>
/// <strong>The lock file holds a hint, never an authority.</strong> After each append the writer records in
/// the lock file where the walk got to (segment, length, sequence, hash), so the next append resumes the
/// walk from there instead of from the start. A writer resumes only when the hint names a listed segment
/// and the record that ends at the recorded length really carries the recorded sequence and hash. Anything
/// else (no hint, an unreadable one, a segment that is gone or shorter, a different record at that point)
/// means a full walk. So editing or deleting the lock file can cost a writer time, but cannot choose what it
/// chains onto or where it writes.
/// </para>
/// <para>
/// <strong>Records cut from the end.</strong> A chain that got shorter is logged at Critical and the walk
/// takes the shorter chain's head, so the next record reuses the removed records' sequence numbers. The
/// chain itself then looks whole; what catches it is the verification checkpoint (#416), which fails when
/// the record it last verified is gone or different. Records added after one verification pass and removed
/// before the next are not caught by either; that needs storage that refuses deletes (WORM).
/// </para>
/// </remarks>
public sealed partial class HashChainedJsonlWriter
{
    /// <summary>How long an append or a verification waits for another process to release the chain's lock.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait between attempts to take the lock.</summary>
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(15);

    /// <summary>The first read size when looking back for the record a hint names.</summary>
    private const int HeadCheckFirstReadBytes = 4 * 1024;

    /// <summary>The longest record the hint check will look back over before giving up and walking.</summary>
    private const int HeadCheckMaxBytes = 1024 * 1024;

    /// <summary>Where the last append left the chain: the hint the next append resumes from.</summary>
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
    /// Works out the head to append after, inside the lock: resumes the walk from the hint when it checks
    /// out against the files, otherwise walks the whole chain.
    /// </summary>
    private async Task<(SharedHead Head, long NextSequence)> LoadHeadAsync(
        FileStream lockFile, IReadOnlyList<string> segments, CancellationToken cancellationToken)
    {
        var hint = await ReadHintAsync(lockFile, cancellationToken).ConfigureAwait(false);
        if (hint is null)
            return await WalkAsync(segments, 0, 0, ScanState.Genesis, cancellationToken).ConfigureAwait(false);

        var index = IndexOfSegment(segments, hint.Segment);
        var check = index < 0
            ? HintCheck.DoesNotMatch
            : await CheckHintAsync(segments[index], hint, cancellationToken).ConfigureAwait(false);

        if (check == HintCheck.Matches)
        {
            return await WalkAsync(
                segments, index, hint.Length, ScanState.FromHead(hint.Sequence, hint.Hash), cancellationToken).ConfigureAwait(false);
        }

        if (check == HintCheck.CannotCheck)
        {
            _logger.LogWarning(
                "Audit chain at {AuditLocation}: its newest record is too long to check quickly; walking the whole chain.",
                _displayLocation);
        }
        else
        {
            _logger.LogCritical(
                "Audit chain at {AuditLocation}: its newest recorded record (sequence {Sequence} in {Segment}) is no longer " +
                "where it was written. Records may have been removed or a segment deleted. Walking the whole chain to find " +
                "its head; verification against the last checkpoint reports any records removed.",
                _displayLocation, hint.Sequence, hint.Segment);
        }

        return await WalkAsync(segments, 0, 0, ScanState.Genesis, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The segments to walk, with the caller's current segment added when it exists but the listing missed it.
    /// A directory listing on a network share can lag behind a file another host just created; walking without
    /// that file would miss its records and hand their sequence numbers out again. The current segment's path
    /// comes from the caller, never from the lock file.
    /// </summary>
    private IReadOnlyList<string> WithCurrentSegment(IReadOnlyList<string> listed)
    {
        var current = _currentSegment();
        if (IndexOfSegment(listed, Path.GetFileName(current)) >= 0 || LengthOf(current) is null)
            return listed;

        var name = Path.GetFileName(current);
        var position = 0;
        while (position < listed.Count && string.CompareOrdinal(Path.GetFileName(listed[position]), name) < 0)
            position++;

        return [.. listed.Take(position), current, .. listed.Skip(position)];
    }

    /// <summary>
    /// The one rule that decides the head: from <paramref name="state"/> at <paramref name="startOffset"/> in
    /// segment <paramref name="startIndex"/>, read every later line to the end of the chain, skipping and
    /// logging lines that do not chain, and return the last record that does with the point the walk reached,
    /// and the sequence the next record takes (never one any line already carries; see
    /// <see cref="ScanState.HighestSequenceSeen"/>).
    /// </summary>
    private async Task<(SharedHead Head, long NextSequence)> WalkAsync(
        IReadOnlyList<string> segments, int startIndex, long startOffset, ScanState state, CancellationToken cancellationToken)
    {
        var reachedSegment = string.Empty;
        long reachedLength = 0;

        for (var i = startIndex; i < segments.Count; i++)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(segments[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            await using (stream)
            {
                // The open handle's length, not a cached directory entry's, so another host's appends are seen.
                var end = stream.Length;
                var start = i == startIndex ? Math.Min(startOffset, end) : 0;
                stream.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(new PrefixStream(stream, end - start));
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    state.LineNumber++;
                    if (!string.IsNullOrWhiteSpace(line) && !ApplyLine(line, ref state, out var skipped))
                    {
                        _logger.LogError(
                            "Audit chain at {AuditLocation}: stepping over a line in {Segment} that does not chain ({Reason}); " +
                            "verification reports it.",
                            _displayLocation, Path.GetFileName(segments[i]), skipped.BreakReason);
                    }
                }

                reachedSegment = Path.GetFileName(segments[i]);
                reachedLength = end;
            }
        }

        return (new SharedHead(reachedSegment, reachedLength, state.LastValidSequence, state.LastValidHash), state.NextSequence);
    }

    /// <summary>The outcome of checking a hint against the files.</summary>
    private enum HintCheck
    {
        /// <summary>The line ending at the hint's length is a self-consistent record with the hint's sequence and hash.</summary>
        Matches,

        /// <summary>It is not: the files changed under the hint, or the hint was edited.</summary>
        DoesNotMatch,

        /// <summary>The record is longer than the check reads back over; nothing is known either way.</summary>
        CannotCheck,
    }

    /// <summary>
    /// Whether the line ending at the hint's length in <paramref name="path"/> is a record carrying the hint's
    /// sequence and hash, and whose hash is right for its own content, so a hint cannot promote a line that does
    /// not hash to what it claims. Reads backward in growing steps, so a check costs about one record.
    /// </summary>
    private static async Task<HintCheck> CheckHintAsync(string path, SharedHead hint, CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return HintCheck.DoesNotMatch;
        }

        await using (stream)
        {
            if (stream.Length < hint.Length)
                return HintCheck.DoesNotMatch;

            for (var size = HeadCheckFirstReadBytes; ; size *= 4)
            {
                var window = (int)Math.Min(hint.Length, Math.Min(size, HeadCheckMaxBytes));
                if (window < 2)
                    return HintCheck.DoesNotMatch;

                var buffer = new byte[window];
                stream.Seek(hint.Length - window, SeekOrigin.Begin);
                await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (buffer[^1] != (byte)'\n')
                    return HintCheck.DoesNotMatch;

                var lineStart = Array.LastIndexOf(buffer, (byte)'\n', window - 2) + 1;
                if (lineStart > 0 || window == hint.Length)
                {
                    var line = Encoding.UTF8.GetString(buffer, lineStart, window - 1 - lineStart);
                    return IsRecord(line, hint) ? HintCheck.Matches : HintCheck.DoesNotMatch;
                }

                if (window >= HeadCheckMaxBytes)
                    return HintCheck.CannotCheck;
            }
        }
    }

    private static bool IsRecord(string line, SharedHead hint) =>
        TryParseLine(line, out var sequence, out var recordHash, out var previousHash, out var payloadJson)
        && sequence == hint.Sequence
        && string.Equals(recordHash, hint.Hash, StringComparison.Ordinal)
        && string.Equals(ComputeRecordHash(sequence, previousHash, payloadJson), recordHash, StringComparison.Ordinal);

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
    /// The segment to append to: the caller's current segment, or the last listed segment when that sorts
    /// later. Never an earlier one than the chain has reached, which would put sequence numbers out of order
    /// on disk. Following a later segment means some writer's clock (or a planted file) is ahead of this
    /// one's; that is logged, and the drift store's date-range reads include later-dated files for it.
    /// </summary>
    private string ChooseTargetSegment(IReadOnlyList<string> segments)
    {
        var current = _currentSegment();
        if (segments.Count == 0)
            return current;

        var last = segments[^1];
        if (string.CompareOrdinal(Path.GetFileName(last), Path.GetFileName(current)) <= 0)
            return current;

        _logger.LogWarning(
            "Audit chain at {AuditLocation}: appending to {Segment}, which sorts after this writer's current segment " +
            "{Current}. Another writer's clock is ahead of this one, or a later-dated file was placed in the folder.",
            _displayLocation, Path.GetFileName(last), Path.GetFileName(current));
        return last;
    }

    /// <summary>
    /// Appends <paramref name="line"/> to <paramref name="path"/> in append mode, first ending any line a crashed
    /// writer left unterminated so the new record never glues onto it. Not cancellable once started: a record
    /// written halfway is exactly the torn line this guards against. Returns the segment's new length.
    /// </summary>
    private static long AppendLine(string path, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        var needsNewline = EndsUnterminated(path);

        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        if (needsNewline)
            stream.WriteByte((byte)'\n');
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return stream.Length;
    }

    private static bool EndsUnterminated(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length == 0)
                return false;

            stream.Seek(-1, SeekOrigin.End);
            return stream.ReadByte() != '\n';
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The hint in the lock file, or <c>null</c> when there is none or it is not well formed.</summary>
    private static async Task<SharedHead?> ReadHintAsync(FileStream lockFile, CancellationToken cancellationToken)
    {
        if (lockFile.Length == 0)
            return null;

        try
        {
            lockFile.Seek(0, SeekOrigin.Begin);
            var hint = await JsonSerializer.DeserializeAsync<SharedHead>(lockFile, cancellationToken: cancellationToken).ConfigureAwait(false);
            // The segment name is only ever compared with listed segments' names, never combined into a path,
            // so a name with separators in it simply matches nothing.
            return hint is { Segment.Length: > 0, Length: > 0, Sequence: >= 0, Hash.Length: 64 } ? hint : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteHint(FileStream lockFile, SharedHead head)
    {
        lockFile.Seek(0, SeekOrigin.Begin);
        lockFile.SetLength(0);
        JsonSerializer.Serialize(lockFile, head);
        lockFile.Flush(flushToDisk: true);
    }

    /// <summary>The length of <paramref name="path"/> read through an open handle, or <c>null</c> if it is gone.</summary>
    private static long? LengthOf(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Length;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}

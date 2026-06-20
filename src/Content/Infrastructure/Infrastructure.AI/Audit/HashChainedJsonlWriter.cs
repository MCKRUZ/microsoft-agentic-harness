using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Domain.AI.Audit;
using Domain.Common;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Append-only JSONL writer that links every record into a cryptographic hash-chain,
/// making the log tamper-evident: any retroactive edit, deletion, or reordering of a
/// record breaks the chain at the first affected record and is detected by
/// <see cref="VerifyChainAsync"/>.
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
/// <b>Framing safety.</b> The framing is line- and tab-delimited. <c>System.Text.Json</c>
/// escapes control characters inside string values (tab/newline become <c>\t</c>/<c>\n</c>),
/// so a serialized JSON payload never contains a raw framing byte. The writer rejects any
/// payload that does contain a raw tab, carriage-return, or newline rather than corrupt the
/// chain by splitting one logical record across physical lines.
/// </para>
/// <para>
/// <b>Trusted head recovery.</b> On first append after a restart the writer rebuilds its
/// in-memory head by scanning the file and validating the chain — the head is set to the last
/// <i>cryptographically valid</i> record, never to an unverified tail. A forged or torn line
/// appended out of band therefore cannot seed the live head; legitimate records continue to
/// chain onto verified state, and the forged/torn line is still surfaced by
/// <see cref="VerifyChainAsync"/>.
/// </para>
/// <para>
/// <b>Brownfield rollout.</b> When pointed at a file that already contains un-chained lines
/// written before this primitive was introduced, those legacy lines are treated as predating
/// the chain: the scan skips them, the chain genesis is the first chained record written after
/// rollout, and verification ignores the leading legacy lines. No silent rewrite of existing
/// history occurs.
/// </para>
/// <para>
/// <b>Out of scope (handled by the verification layer).</b> A torn trailing line from a crash
/// mid-write, or wholesale deletion of the file, are not concealable by this primitive but are
/// not <i>repaired</i> by it either — those are the province of WORM storage and the scheduled
/// chain-verification job. This primitive guarantees detection, not crash-safe truncation.
/// </para>
/// <para>
/// <b>Cost note.</b> Head recovery scans the file once per process lifetime (on first append);
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

    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private bool _headInitialized;
    private long _headSequence = -1;
    private string _headHash = GenesisHash;

    /// <summary>Initializes a new <see cref="HashChainedJsonlWriter"/>.</summary>
    /// <param name="filePath">Absolute path to the JSONL file backing this chain.</param>
    /// <param name="logger">Logger for operational diagnostics.</param>
    public HashChainedJsonlWriter(string filePath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);

        _filePath = filePath;
        _logger = logger;
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
            await EnsureHeadInitializedAsync(cancellationToken).ConfigureAwait(false);

            var sequence = _headSequence + 1;
            var previousHash = _headHash;
            var recordHash = ComputeRecordHash(sequence, previousHash, payloadJson);
            var line = string.Concat(
                payloadJson, FieldSeparator,
                recordHash, FieldSeparator,
                previousHash, FieldSeparator,
                sequence.ToString(CultureInfo.InvariantCulture), "\n");

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            await using (var stream = new FileStream(
                _filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            _headSequence = sequence;
            _headHash = recordHash;
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to append audit record to {FilePath}", _filePath);
            return Result.Fail($"Failed to persist audit record: {ex.Message}");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Walks the chain from genesis and recomputes every link, reporting whether the log is
    /// intact and, if not, the first record where it broke.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A verification result describing the chain's integrity.</returns>
    public async Task<AuditChainVerificationResult> VerifyChainAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scan = await ScanChainAsync(cancellationToken).ConfigureAwait(false);
            return scan.BreakSequence is { } brokenSequence
                ? AuditChainVerificationResult.Broken(scan.VerifiedCount, brokenSequence, scan.BreakReason!)
                : AuditChainVerificationResult.Valid(scan.VerifiedCount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to read audit chain at {FilePath}", _filePath);
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
    /// Recovers the chain head (last valid sequence and hash) by scanning and validating the
    /// file once. The head is the tail of the longest valid prefix, so appends always continue
    /// from cryptographically verified state. Runs once per process under the append semaphore.
    /// </summary>
    private async Task EnsureHeadInitializedAsync(CancellationToken cancellationToken)
    {
        if (_headInitialized)
            return;

        var scan = await ScanChainAsync(cancellationToken).ConfigureAwait(false);
        _headSequence = scan.LastValidSequence;
        _headHash = scan.LastValidHash;
        _headInitialized = true;
    }

    /// <summary>
    /// Single forward pass over the file shared by verification and head recovery. Validates
    /// sequence continuity, previous-hash links, and record hashes from genesis, tolerating
    /// only the leading legacy (pre-chain) lines.
    /// </summary>
    private async Task<ChainScan> ScanChainAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
            return ChainScan.Empty;

        long expectedSequence = 0;
        var expectedPreviousHash = GenesisHash;
        long verifiedCount = 0;
        long lastValidSequence = -1;
        var lastValidHash = GenesisHash;
        var chainStarted = false;
        var lineNumber = 0;

        await using var stream = new FileStream(
            _filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!TryParseLine(line, out var sequence, out var recordHash, out var previousHash, out var payloadJson))
            {
                // Legacy lines that predate the chain are tolerated only before it starts.
                if (chainStarted)
                    return ChainScan.Break(verifiedCount, expectedSequence, lastValidSequence, lastValidHash,
                        $"Malformed chain line at line {lineNumber}.");
                continue;
            }

            chainStarted = true;

            if (sequence != expectedSequence)
                return ChainScan.Break(verifiedCount, expectedSequence, lastValidSequence, lastValidHash,
                    $"Sequence gap: expected {expectedSequence} but found {sequence} at line {lineNumber} (record deleted or reordered).");

            if (!string.Equals(previousHash, expectedPreviousHash, StringComparison.Ordinal))
                return ChainScan.Break(verifiedCount, sequence, lastValidSequence, lastValidHash,
                    $"Previous-hash mismatch at sequence {sequence} (chain link altered).");

            var computedHash = ComputeRecordHash(sequence, previousHash, payloadJson);
            if (!string.Equals(computedHash, recordHash, StringComparison.Ordinal))
                return ChainScan.Break(verifiedCount, sequence, lastValidSequence, lastValidHash,
                    $"Record-hash mismatch at sequence {sequence} (record content altered).");

            verifiedCount++;
            lastValidSequence = sequence;
            lastValidHash = recordHash;
            expectedSequence = sequence + 1;
            expectedPreviousHash = recordHash;
        }

        return ChainScan.Valid(verifiedCount, lastValidSequence, lastValidHash);
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

    /// <summary>Outcome of a single forward pass over the chain file.</summary>
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
        public static ChainScan Empty { get; } = Valid(0, -1, GenesisHash);

        public static ChainScan Valid(long verifiedCount, long lastValidSequence, string lastValidHash) =>
            new(verifiedCount, lastValidSequence, lastValidHash, null, null);

        public static ChainScan Break(
            long verifiedCount, long breakSequence, long lastValidSequence, string lastValidHash, string reason) =>
            new(verifiedCount, lastValidSequence, lastValidHash, breakSequence, reason);
    }
}

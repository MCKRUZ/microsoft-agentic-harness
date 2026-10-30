using System.Diagnostics;
using System.Globalization;
using Domain.AI.Audit;
using Domain.Common;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>
/// The append half of <see cref="HashChainedJsonlWriter"/>: framing checks, the waits for the chain, and handing a record
/// that could not be appended to the pending folder (#999, #1001).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two waits.</strong> An append first waits for this process's other appends and verifications (one in-process
/// lock; a verification holds it for its whole walk), then for any other process (the lock file). By default the first wait
/// has no limit, as it always had, and the second gives up after ten seconds. A caller that answers a person can bound
/// both with one deadline instead, and let the record be held.
/// </para>
/// <para>
/// <strong>What is held.</strong> Usually the record itself. A trail that refuses the action when its record cannot be
/// appended passes the same record marked as not carried out, so replay never writes a decision the trail shows as acted on
/// when the action was refused.
/// </para>
/// </remarks>
public sealed partial class HashChainedJsonlWriter
{
    /// <summary>
    /// Appends a serialized record to the chain, computing and persisting its hash links. Waits as long as it takes for this
    /// process's other appends and verifications, and up to ten seconds for any other process writing the chain.
    /// </summary>
    /// <param name="payloadJson">The caller's already-serialized record as a single-line JSON string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when the record was persisted; a failure result when the
    /// payload contains a raw framing character or the file could not be written. A writer built from an
    /// <see cref="AuditChainLocation"/> holds a record it could not write for replay, and its failure says so (#1001).
    /// </returns>
    public Task<Result> AppendAsync(string payloadJson, CancellationToken cancellationToken) =>
        AppendCoreAsync(payloadJson, null, lockWait: null, cancellationToken);

    /// <summary>
    /// Appends a serialized record as <see cref="AppendAsync(string, CancellationToken)"/> does, waiting no longer than
    /// <paramref name="lockWait"/> in all for the chain: for this process's other appends and verifications, then for any
    /// other process (#999).
    /// </summary>
    /// <param name="payloadJson">The caller's already-serialized record as a single-line JSON string.</param>
    /// <param name="lockWait">
    /// The longest the append waits for the chain. A caller answering a person keeps it short and lets a writer with a
    /// pending folder hold the record for replay instead of making the person wait.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>As <see cref="AppendAsync(string, CancellationToken)"/>.</returns>
    public Task<Result> AppendAsync(string payloadJson, TimeSpan lockWait, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lockWait, TimeSpan.Zero);
        return AppendCoreAsync(payloadJson, null, lockWait, cancellationToken);
    }

    /// <summary>
    /// Appends a serialized record as <see cref="AppendAsync(string, CancellationToken)"/> does, but holds what
    /// <paramref name="payloadIfHeld"/> makes in its place when the append fails: for a trail that refuses the action its
    /// record could not be written for, the same record marked as not carried out. Called only on failure.
    /// </summary>
    /// <param name="payloadJson">The record as it is appended when the chain accepts it.</param>
    /// <param name="payloadIfHeld">Makes the record as it is held, and later replayed, when the chain does not.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>As <see cref="AppendAsync(string, CancellationToken)"/>.</returns>
    public Task<Result> AppendAsync(string payloadJson, Func<string> payloadIfHeld, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadIfHeld);
        return AppendCoreAsync(payloadJson, payloadIfHeld, lockWait: null, cancellationToken);
    }

    /// <summary>
    /// Appends a serialized record as <see cref="AppendAsync(string, CancellationToken)"/> does, but never holds it when the
    /// append fails: the failure goes back to the caller and nothing reaches the pending folder.
    /// </summary>
    /// <remarks>
    /// For a caller that owns its own durable retry of the record, or that refuses the action so the record describes nothing
    /// that happened. The escalation service is both: it re-drives a resolution's outcome from its own store until the
    /// append succeeds, and it does not open a request or apply a decision whose record failed. Holding the record as well
    /// would put it in the trail twice once replay wrote it, with different timestamps, so replay's whole-line match could
    /// not tell the copies apart.
    /// </remarks>
    /// <param name="payloadJson">The caller's already-serialized record as a single-line JSON string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>As <see cref="AppendAsync(string, CancellationToken)"/>, except that a failure is never held.</returns>
    public Task<Result> AppendWithoutHoldingAsync(string payloadJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        return payloadJson.AsSpan().IndexOfAny(FramingChars) >= 0
            ? Task.FromResult(Result.Fail(
                "Audit payload must not contain raw tab, carriage-return, or newline characters; chain framing is line- and tab-delimited."))
            : AppendOnceAsync(payloadJson, lockWait: null, cancellationToken);
    }

    /// <summary>
    /// Appends a held record, waiting at most <see cref="LockTimeout"/> in all so one slow verification cannot stall a
    /// replay pass that holds the pending folder, and never holds it again when the append fails.
    /// </summary>
    internal Task<Result> AppendHeldRecordAsync(string payloadJson, CancellationToken cancellationToken) =>
        AppendOnceAsync(payloadJson, LockTimeout, cancellationToken);

    /// <remarks>
    /// A record whose append throws (the writer disposed at shutdown, the caller cancelling) is held as well before the
    /// exception goes on to the caller: whatever the record describes has already happened.
    /// </remarks>
    private async Task<Result> AppendCoreAsync(
        string payloadJson, Func<string>? payloadIfHeld, TimeSpan? lockWait, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);

        // Not held: replay could never append it either.
        if (payloadJson.AsSpan().IndexOfAny(FramingChars) >= 0)
            return Result.Fail(
                "Audit payload must not contain raw tab, carriage-return, or newline characters; chain framing is line- and tab-delimited.");

        if (_pending is null)
            return await AppendOnceAsync(payloadJson, lockWait, cancellationToken).ConfigureAwait(false);

        Result appended;
        try
        {
            appended = await AppendOnceAsync(payloadJson, lockWait, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pending.Hold(payloadIfHeld?.Invoke() ?? payloadJson, [$"The append threw {ex.GetType().Name}."]);
            throw;
        }

        return appended.IsSuccess ? appended : _pending.Hold(payloadIfHeld?.Invoke() ?? payloadJson, appended.Errors);
    }

    /// <summary>
    /// One attempt at the append, never held. With no <paramref name="lockWait"/>, the in-process wait has no limit and the
    /// cross-process wait is <see cref="LockTimeout"/>; with one, both share that deadline.
    /// </summary>
    private async Task<Result> AppendOnceAsync(string payloadJson, TimeSpan? lockWait, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        if (!await _semaphore.WaitAsync(lockWait ?? Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false))
            return Busy(new TimeoutException("Another append or a verification in this process still holds the chain."), lockWait!.Value);

        var fileWait = lockWait is { } bound ? (bound > waited.Elapsed ? bound - waited.Elapsed : TimeSpan.Zero) : LockTimeout;
        try
        {
            // Across processes: no other writer appends between reading the head and recording the new one.
            await using var chainLock = await AcquireChainLockAsync(fileWait, cancellationToken).ConfigureAwait(false);
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
            TryWriteHint(chainLock, new SharedHead(Path.GetFileName(segmentPath), lengthAfterWrite, sequence, recordHash));
            return Result.Success();
        }
        catch (AuditChainBusyException ex)
        {
            return Busy(ex, lockWait ?? LockTimeout);
        }
        catch (AuditFolderRefusedException ex)
        {
            _logger.LogCritical(ex, "Audit record not written to {AuditLocation}: the folder is refused", _displayLocation);
            return Result.Fail($"Failed to persist audit record: {ex.Message}");
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

    // The record is in the chain once AppendLine returns. A hint that cannot be written only makes the next append walk the
    // chain (the hint is never an authority), so failing here would report, and hold, a record the chain already has.
    private void TryWriteHint(FileStream chainLock, SharedHead head)
    {
        try
        {
            WriteHint(chainLock, head);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Audit record appended to {AuditLocation}, but its resume hint could not be written", _displayLocation);
        }
    }

    // A busy chain is routine for a writer that holds the record for replay, and a fault for one that loses it.
    private Result Busy(Exception ex, TimeSpan waited)
    {
        _logger.Log(_pending is null ? LogLevel.Error : LogLevel.Warning, ex,
            "Audit record not written to {AuditLocation}: the chain was still busy after {LockWait}", _displayLocation, waited);
        return Result.Fail("Failed to persist audit record: the audit chain is busy. Retry the operation.");
    }
}

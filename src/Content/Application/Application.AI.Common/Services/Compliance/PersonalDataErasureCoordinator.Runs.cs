using Application.AI.Common.Exceptions;
using Domain.Common.Compliance;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Compliance;

/// <summary>
/// The run store's part in a request: the person's live workflow and evaluation runs are cancelled first (#992), the store
/// then erases the finished ones (#926), and the receipt says when a record was still kept.
/// </summary>
public sealed partial class PersonalDataErasureCoordinator
{
    private const string KeptRunsStore = "workflow and evaluation runs still held";
    private const string KeptRunsReason =
        "a run record of the person's was kept: a workflow or evaluation run that was still executing when erasure stopped waiting for it, one that ended or started while erasure ran, or one whose run-scoped state could not be released; run erasure again once the person's runs have ended";

    /// <summary>
    /// Cancels the person's queued and parked runs through the path an owner's cancel takes, withdrawing their approvals,
    /// and signals their executing workflows. A failure is logged and the request goes on: whatever was not cancelled is
    /// still held when <see cref="NoteRunsKeptAsync"/> counts, so the receipt cannot read <c>Full</c> over it.
    /// </summary>
    private async Task CancelLiveRunsAsync(string ownerId)
    {
        if (_liveRuns is null)
            return;

        try
        {
            await _liveRuns.CancelRunsAsync(ownerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(MessageWithheldException.From(ex), "Erasure could not cancel the person's runs.");
        }
    }

    /// <summary>
    /// Waits a bounded time for the person's runs still executing, alongside the planned-task stop. A failure is contained
    /// the same way as <see cref="CancelLiveRunsAsync"/>.
    /// </summary>
    private async Task WaitForLiveRunsAsync(string ownerId, CancellationToken cancellationToken)
    {
        if (_liveRuns is null)
            return;

        try
        {
            var stillLive = await _liveRuns.WaitForRunsAsync(ownerId, cancellationToken);
            if (stillLive > 0)
                _logger.LogWarning("Erasure went on with {Count} run(s) not yet ended; their records will be kept.", stillLive);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(MessageWithheldException.From(ex), "Erasure could not wait for the person's runs.");
        }
    }

    /// <summary>
    /// Lists the person's run records the store still holds once every stage has run. Counting every record, not only the
    /// live ones, covers a run that ended after the store was erased: its record is not live, but it is still held. A line
    /// of its own, added after the stages, so it neither holds a stage back nor keeps the journal: nothing else is found
    /// through a run record. A failure to count is its own failed line, since the deletes are already done.
    /// </summary>
    private async Task NoteRunsKeptAsync(string ownerId, List<StoreErasureOutcome> outcomes)
    {
        if (_keptRuns is null)
            return;

        int kept;
        try
        {
            kept = await _keptRuns.CountRunsKeptAsync(ownerId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(MessageWithheldException.From(ex), "Erasure could not check for run records still held.");
            outcomes.Add(new StoreErasureOutcome(KeptRunsStore, StoreErasureStatus.Failed, 0, FailedReason));
            return;
        }

        if (kept > 0)
            outcomes.Add(new StoreErasureOutcome(KeptRunsStore, StoreErasureStatus.Skipped, 0, KeptRunsReason));
    }
}

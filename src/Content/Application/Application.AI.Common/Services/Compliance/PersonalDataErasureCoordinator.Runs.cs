using Application.AI.Common.Exceptions;
using Domain.Common.Compliance;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Compliance;

/// <summary>
/// The run stores' part in a request: the person's live workflow and evaluation runs are cancelled first (#992), each
/// store then erases the finished ones (#926, #990), and the receipt says, per store, when a record was still kept.
/// </summary>
public sealed partial class PersonalDataErasureCoordinator
{
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
    /// Lists, for each source, the person's records it still holds once every stage has run. Counting every record, not
    /// only the live ones, covers a run that ended after the store was erased: its record is not live, but it is still
    /// held. A line of its own per source, added after the stages, so it neither holds a stage back nor keeps the journal:
    /// nothing else is found through a run record. A failure to count is that source's failed line, since the deletes are
    /// already done, and does not stop the other sources being asked.
    /// </summary>
    private async Task NoteRunsKeptAsync(string ownerId, List<StoreErasureOutcome> outcomes)
    {
        foreach (var source in _keptRuns)
        {
            int kept;
            try
            {
                kept = await source.CountRunsKeptAsync(ownerId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(MessageWithheldException.From(ex), "Erasure could not check for run records still held.");
                outcomes.Add(new StoreErasureOutcome(source.KeptRunsStoreName, StoreErasureStatus.Failed, 0, FailedReason));
                continue;
            }

            if (kept > 0)
                outcomes.Add(new StoreErasureOutcome(source.KeptRunsStoreName, StoreErasureStatus.Skipped, 0, source.KeptRunsReason));
        }
    }
}

using Application.AI.Common.Exceptions;
using Domain.Common.Compliance;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Compliance;

/// <summary>
/// The run store's part in a request (#926): it erases the person's finished workflow and evaluation runs and keeps the
/// live ones, so the receipt must say when one was kept.
/// </summary>
public sealed partial class PersonalDataErasureCoordinator
{
    private const string LiveRunsStore = "workflow and evaluation runs still in progress";
    private const string LiveRunsReason =
        "a workflow or evaluation run was still queued, running or waiting for an approval; its run record was kept until it ends, so run erasure again";

    /// <summary>
    /// Lists the person's runs that had not ended when the run store was erased. A line of its own, added after every
    /// stage, so it neither holds a stage back nor keeps the journal: nothing else is found through a run record. A failure
    /// to count is its own failed line, since the deletes are already done.
    /// </summary>
    private async Task NoteRunsStillLiveAsync(string ownerId, List<StoreErasureOutcome> outcomes)
    {
        if (_liveRuns is null)
            return;

        int live;
        try
        {
            live = await _liveRuns.CountLiveRunsAsync(ownerId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(MessageWithheldException.From(ex), "Erasure could not check for runs still in progress.");
            outcomes.Add(new StoreErasureOutcome(LiveRunsStore, StoreErasureStatus.Failed, 0, FailedReason));
            return;
        }

        if (live > 0)
            outcomes.Add(new StoreErasureOutcome(LiveRunsStore, StoreErasureStatus.Skipped, 0, LiveRunsReason));
    }
}

using Application.AI.Common.Exceptions;
using Domain.Common.Compliance;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Compliance;

/// <summary>
/// The run store's part in a request (#926): it erases the person's finished workflow and evaluation runs and keeps the
/// live ones, so the receipt must say when a record was kept.
/// </summary>
public sealed partial class PersonalDataErasureCoordinator
{
    private const string KeptRunsStore = "workflow and evaluation runs still held";
    private const string KeptRunsReason =
        "a workflow or evaluation run had not ended when its store was erased, so its run record was kept; run erasure again once it has ended";

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

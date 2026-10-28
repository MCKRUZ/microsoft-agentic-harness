using Application.AI.Common.Exceptions;
using Domain.Common.Compliance;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Compliance;

/// <summary>
/// The run stores' part in a request (#926, #990): each erases the person's finished runs and keeps the live ones, so the
/// receipt must say when a record was kept.
/// </summary>
public sealed partial class PersonalDataErasureCoordinator
{
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

using Application.AI.Common.Exceptions;
using Domain.Common.Compliance;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Compliance;

/// <summary>
/// The erasure journal's part in a request (#907): read what earlier requests could not finish with, record this
/// request's ids before any lead is deleted, and clear them once every store that reads them has succeeded.
/// </summary>
public sealed partial class PersonalDataErasureCoordinator
{
    private const string JournalReadStore = "erasure journal (earlier requests)";
    private const string JournalWriteStore = "erasure journal";
    private const string JournalReadFailedReason =
        "what earlier erasure requests could not finish with could not be read; it stays recorded, so run erasure again";
    private const string JournalKeptStore = "erasure journal entries";
    private const string JournalKeptReason =
        "the person's id and the conversation and planned-task ids this request read are kept in the erasure journal until a later request finishes with them, or, for conversations whose records were kept, until the journal's retention runs out";
    private const string JournalWriteFailedReason =
        "the ids this request read could not be recorded, so the conversations and plans were kept; a store that failed after deleting them could not otherwise be retried, so run erasure again";

    /// <summary>
    /// The ids earlier requests for <paramref name="ownerId"/> recorded and did not clear. A journal that cannot be read
    /// adds a failed line and contributes nothing: what it holds stays recorded for the next request, so this one need not
    /// hold anything back.
    /// </summary>
    private async Task<ErasureJournalEntries> ReadJournalAsync(
        string ownerId, List<StoreErasureOutcome> outcomes, CancellationToken cancellationToken)
    {
        if (_journal is null)
            return ErasureJournalEntries.Empty;

        try
        {
            return await _journal.ReadAsync(ownerId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(MessageWithheldException.From(ex), "Erasure could not read its journal.");
            outcomes.Add(new StoreErasureOutcome(JournalReadStore, StoreErasureStatus.Failed, 0, JournalReadFailedReason));
            return ErasureJournalEntries.Empty;
        }
    }

    /// <summary>
    /// Records the subject's ids as this request's before any conversation or plan is deleted. Returns whether they are
    /// recorded (or there is no journal); on <see langword="false"/> the caller deletes no lead, because a store failing
    /// after that deletion could not be retried.
    /// </summary>
    private async Task<bool> RecordJournalAsync(
        string ownerId, string requestId, ErasureSubject subject, List<StoreErasureOutcome> outcomes)
    {
        if (_journal is null)
            return true;

        var entries = new ErasureJournalEntries(
            [.. subject.ConversationIds.Except(subject.WithheldConversationIds, StringComparer.Ordinal)],
            subject.WithheldConversationIds,
            subject.PlanRuns);
        if (entries.IsEmpty)
            return true;

        try
        {
            // Not interrupted by the caller going away: half a record is the case this exists to prevent.
            await _journal.RecordAsync(ownerId, requestId, entries, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(MessageWithheldException.From(ex), "Erasure could not record its journal.");
            outcomes.Add(new StoreErasureOutcome(JournalWriteStore, StoreErasureStatus.Failed, 0, JournalWriteFailedReason));
            return false;
        }
    }

    /// <summary>
    /// Clears this request's entries and returns whether it did. A failure is only logged: the entries stay, the next
    /// request merges them in again, and erasing what is already gone does no harm.
    /// </summary>
    private async Task<bool> ClearJournalAsync(string ownerId, string requestId)
    {
        if (_journal is null)
            return true;

        try
        {
            await _journal.ClearAsync(ownerId, requestId, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(MessageWithheldException.From(ex), "Erasure could not clear its journal; the next request will retry it.");
            return false;
        }
    }

    /// <summary>
    /// Says on the receipt when the journal still holds the person's ids: those of this request when it could not finish,
    /// and their withheld conversation ids, which age out instead. The journal is kept by design, with an end date, so the
    /// line does not by itself make the receipt partial; whatever kept it there already has its own line.
    /// </summary>
    private void NoteJournalKept(bool requestKept, ErasureSubject subject, List<StoreErasureOutcome> outcomes)
    {
        var recordedAnything = subject.ConversationIds.Count > 0 || !subject.PlanRuns.IsEmpty;
        if (_journal is null || (!(requestKept && recordedAnything) && subject.WithheldConversationIds.Count == 0))
            return;

        outcomes.Add(new StoreErasureOutcome(JournalKeptStore, StoreErasureStatus.RetainedUntilExpiry, 0, JournalKeptReason));
    }

    /// <summary>
    /// Whether every registered store either erased or cannot erase at all. Judged on the stores' own lines, not on the
    /// receipt's completeness: a store kept forever, or a withheld conversation, makes the receipt partial without
    /// leaving anything that only these ids reach.
    /// </summary>
    private bool EveryEraserSucceeded(IReadOnlyList<StoreErasureOutcome> outcomes)
    {
        var stores = _erasers.Select(e => e.ErasureStoreName).ToHashSet(StringComparer.Ordinal);
        return outcomes
            .Where(o => stores.Contains(o.Store))
            .All(o => o.Status is StoreErasureStatus.Erased or StoreErasureStatus.NotSupported);
    }
}

using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Compliance;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Compliance;

/// <summary>
/// Erases one person from the knowledge graph and from every registered <see cref="IPersonalDataEraser"/>, in the
/// order that keeps a failed request retryable, and folds every store's result into one receipt (#900).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Order.</strong> The subject's conversation ids and the ids their planned-task runs used are read first, before
/// anything is deleted, because several stores carry only such an id. Then the knowledge graph and every
/// <see cref="ErasureStage.BeforeConversations"/> store run. The conversations, then the plans, are erased next, only when
/// every earlier store succeeded: deleting them after a failure would leave the failed store's data with nothing left
/// that leads to it. The stage after acts only on the ids of whichever of the two was erased, so one failing never
/// strands the other's records. <see cref="ErasureStage.AfterConversations"/>
/// stores run last, only when the conversations are gone. Every <see cref="IPersonalDataScrubber"/> then clears the bytes
/// its deletes left in its files, on every request, so a store whose files are busy fails only its own line.
/// </para>
/// <para>
/// <strong>What <c>Full</c> means.</strong> Every store that can find the subject's data removed it, and no store the host
/// runs can still hold it with no end date, other than the tamper-evident audit trails. Stores that cannot be searched
/// for one person (logs, traces, the response cache, the audit trails) are listed on the receipt from their
/// <see cref="RetainedPersonalData"/> registrations, each with how long it keeps what it holds. One that nothing ages out
/// (<see cref="StoreErasureStatus.RetainedIndefinitely"/>) makes the receipt <c>Partial</c>; one whose records go away on
/// their own is listed without doing so, and so is an audit trail kept on purpose, whose line says what kinds of data it
/// holds so the receipt does not imply it holds nothing.
/// </para>
/// <para>
/// <strong>Runs system-scoped.</strong> The ambient request scope is suspended for the whole request, as for background
/// retention. Scope-filtering stores such as the tenant-isolated graph answer by the caller's identity, so under an
/// operator's scope they would find none of the subject's data and the receipt would still read <c>Full</c>. Deciding who
/// may be erased is the caller's job: the self-service handler passes the caller's own id and the operator handler sits
/// behind a dedicated role.
/// </para>
/// <para>
/// <strong>Failures are contained per store.</strong> One store throwing is recorded as <see cref="StoreErasureStatus.Failed"/>
/// with a fixed phrase (the exception goes to the log only, because store exceptions carry paths and connection
/// strings) and the request carries on, so one broken store does not shield the rest of the subject's data.
/// </para>
/// </remarks>
public sealed partial class PersonalDataErasureCoordinator : IPersonalDataErasure
{
    private const string GraphStoreName = "knowledge graph";
    private const string FailedReason = "the store could not be erased; the failure has been logged";
    private const string StartedMeanwhileReason =
        "the person started a conversation while erasure ran; it and what it produced were kept, so run erasure again";
    private const string PartlyHeldBackReason =
        "partly kept: what the erased conversations or plans led to was removed, but what a store that failed, or a planned task still running, leads to was kept so a retry can still find it";
    private const string RunMeanwhileReason =
        "a planned task was running or was run while erasure ran; its plan and what it recorded since were kept, so run erasure again";
    private const string ScrubFailedReason =
        "the records were deleted, but their bytes could not yet be cleared from the store's files; the failure has been logged, so run erasure again";
    private const string WithheldReason =
        "the person had a conversation whose id is shaped like a planned task's; the conversation was erased, but what other stores hold under its id was kept, because those stores record no owner and the records may be another person's planned task";
    private const string UnreadableReason =
        "some stored conversations could not be read, so whether they are this person's is unknown; they and what they lead to were kept, so fix the store and run erasure again";
    private const string HeldBackReason =
        "kept because an earlier store failed; erasing it now would leave that store's data with nothing that leads to it, or re-arm a once-only tool in a surviving conversation";

    private readonly IErasureOrchestrator _graph;
    private readonly IConversationStore? _conversations;
    private readonly IPlanRunTraceSource? _planRuns;
    private readonly IErasureJournal? _journal;
    private readonly IPlannedTaskCanceller? _runningTasks;
    private readonly IKeptRunSource? _keptRuns;
    private readonly IReadOnlyList<IPersonalDataEraser> _erasers;
    private readonly IReadOnlyList<RetainedPersonalData> _retained;
    private readonly IAmbientRequestScope _ambientScope;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PersonalDataErasureCoordinator> _logger;

    /// <summary>Initializes a new instance of the <see cref="PersonalDataErasureCoordinator"/> class.</summary>
    /// <param name="graph">Erases the subject's knowledge-graph nodes, edges, memories and derived document content.</param>
    /// <param name="erasers">Every registered store that can erase one person.</param>
    /// <param name="retained">Every registered store that keeps personal data it cannot search for one person.</param>
    /// <param name="ambientScope">Suspended for the request, so scope-filtering stores see the subject's data.</param>
    /// <param name="timeProvider">Time source for the receipt's completion time.</param>
    /// <param name="logger">Logger for store failures, which never reach the receipt.</param>
    /// <param name="conversations">
    /// The conversation store, used to read the subject's conversation ids before anything is deleted.
    /// <see langword="null"/> in a host without conversations, in which case there is nothing to find through them.
    /// </param>
    /// <param name="planRuns">
    /// The plan store's record of the subject's planned-task runs, read before anything is deleted (#906).
    /// <see langword="null"/> in a host without plans.
    /// </param>
    /// <param name="journal">
    /// Keeps the ids each request read until every store that reads them has succeeded, so a request that fails after
    /// deleting the conversations or plans can be retried (#907). <see langword="null"/> in a host without one, in which
    /// case such a failure strands the failed store's rows, as before.
    /// </param>
    /// <param name="runningTasks">
    /// Stops the person's planned tasks still running when the request starts, before anything is read (#907).
    /// <see langword="null"/> in a host without plans.
    /// </param>
    /// <param name="keptRuns">
    /// Counts the person's workflow and evaluation run records still held after the run store's erasure, which keeps a
    /// run until it ends (#926). <see langword="null"/> in a host without a run store.
    /// </param>
    public PersonalDataErasureCoordinator(
        IErasureOrchestrator graph,
        IEnumerable<IPersonalDataEraser> erasers,
        IEnumerable<RetainedPersonalData> retained,
        IAmbientRequestScope ambientScope,
        TimeProvider timeProvider,
        ILogger<PersonalDataErasureCoordinator> logger,
        IConversationStore? conversations = null,
        IPlanRunTraceSource? planRuns = null,
        IErasureJournal? journal = null,
        IPlannedTaskCanceller? runningTasks = null,
        IKeptRunSource? keptRuns = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(erasers);
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentNullException.ThrowIfNull(ambientScope);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _graph = graph;
        // A forwarded registration yields null when the host's store cannot erase; see AddPersonalDataEraser.
        _erasers = erasers.OfType<IPersonalDataEraser>().ToList();
        _retained = retained.ToList();
        _ambientScope = ambientScope;
        _timeProvider = timeProvider;
        _logger = logger;
        _conversations = conversations;
        _planRuns = planRuns;
        _journal = journal;
        _runningTasks = runningTasks;
        _keptRuns = keptRuns;
    }

    /// <inheritdoc />
    public async Task<ErasureReceipt> EraseOwnerAsync(string ownerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        using var systemScoped = _ambientScope.SuspendScope();

        // One request per person at a time (#907): overlapping ones could clear an id between another's read and record.
        using var turn = _journal is null ? null : await _journal.BeginRequestAsync(ownerId, cancellationToken);

        // #907: a planned task already running would keep writing under the ids read below after their stores are cleared.
        await StopRunningTasksAsync(ownerId, cancellationToken);

        // Read before any deletion. A failure here erases nothing, which is the only safe answer: without the ids the
        // stores reached through conversations and planned-task runs are unreachable, and what leads to them must not be
        // deleted either.
        var conversations = await ListConversationIdsAsync(ownerId, cancellationToken);
        var planRuns = _planRuns is null
            ? PlanRunTraces.Empty
            : await _planRuns.ListRunTracesAsync(ownerId, cancellationToken);

        // What an earlier request read and could not finish with (#907): its conversations and plans may already be gone,
        // and these ids are then the only way to what they led to.
        var outcomes = new List<StoreErasureOutcome>();
        var journaled = await ReadJournalAsync(ownerId, outcomes, cancellationToken);
        var subject = new ErasureSubject(
            ownerId,
            [.. conversations.Ids.Union(journaled.ConversationIds).Union(journaled.WithheldConversationIds)],
            journaled.PlanRuns.IsEmpty ? planRuns : planRuns.Union(journaled.PlanRuns));
        var requestId = Guid.NewGuid().ToString("N");

        var requestedAt = _timeProvider.GetUtcNow();
        var (graphReceipt, graphOutcome) = await EraseGraphAsync(ownerId, requestedAt, cancellationToken);

        foreach (var eraser in _erasers.Where(e => e.ErasureStage == ErasureStage.BeforeConversations))
            outcomes.Add(await EraseAsync(eraser, subject, cancellationToken));

        // The graph is not reached through conversations, so its failure does not hold them back.
        if (graphOutcome is not null)
            outcomes.Insert(0, graphOutcome);

        // A store that cannot erase at all will not succeed on a retry either, so it does not hold the conversations back.
        // A journal that could not be read does not either: what it held stays recorded for the next request.
        var beforeSucceeded = outcomes.All(o => o.Store is GraphStoreName or JournalReadStore
            || o.Status is StoreErasureStatus.Erased or StoreErasureStatus.NotSupported);

        // Recorded before any lead is deleted; if it cannot be, no lead is deleted.
        var journalRecorded = beforeSucceeded && await RecordJournalAsync(ownerId, requestId, subject, outcomes);
        beforeSucceeded &= journalRecorded;

        // From here on, deletion is not interrupted by the caller going away: a request cancelled between the
        // conversations and the stage after them would leave that stage's records with nothing that leads to them.
        var conversationsErased = await RunStageAsync(ErasureStage.Conversations, beforeSucceeded, outcomes, subject, CancellationToken.None);
        var plansErased = await RunStageAsync(ErasureStage.PlannedTasks, beforeSucceeded, outcomes, subject, CancellationToken.None);

        // The stage after acts only on what an erased lead led to. A host whose conversation store cannot erase leaves the
        // conversations standing, and they keep their ledger rows; a plan store that failed keeps its runs' rows. Each
        // lead that is gone (or never existed) contributes its ids, so one failing never strands the other's records.
        // What the plan store still records after its stage (a run in progress, or made meanwhile) is left out too: its
        // budget and once-only claims are live.
        var conversationsGone = subject.ConversationIds.Count == 0
            || (conversationsErased && _erasers.Any(e => e.ErasureStage == ErasureStage.Conversations));
        var stillRecorded = plansErased ? await PlannedTasksStillRecordedAsync(ownerId, outcomes) : PlanRunTraces.Empty;
        var plannedTasksGone = subject.PlanRuns.IsEmpty
            || (plansErased && stillRecorded is not null && _erasers.Any(e => e.ErasureStage == ErasureStage.PlannedTasks));
        var plannedPart = plannedTasksGone ? subject.PlanRuns.Except(stillRecorded ?? PlanRunTraces.Empty) : PlanRunTraces.Empty;
        // A conversation the store could not read may be the person's and is still there (#907), so a store that erases
        // the whole person is held back too: it would take that conversation's records with nothing left that leads to them.
        var partly = !conversationsGone || !plannedTasksGone || stillRecorded is not { IsEmpty: true }
            || conversations.Unreadable > 0;
        await RunStageAfterAsync(
            new ErasureSubject(ownerId, conversationsGone ? subject.ConversationIds : [], plannedPart),
            beforeSucceeded, partly, outcomes);

        if (conversationsErased)
            await NoteConversationsStartedMeanwhileAsync(ownerId, outcomes);

        await NoteRunsKeptAsync(ownerId, outcomes);

        // #907: never followed into the stores without an owner, so the receipt must not read Full.
        if (subject.WithheldConversationIds.Count > 0)
        {
            outcomes.Add(new StoreErasureOutcome(
                "conversations with a reserved id", StoreErasureStatus.Skipped, 0, WithheldReason));
        }

        // #907: a record the store could not read may be the person's; it and what it leads to were left untouched.
        if (conversations.Unreadable > 0)
        {
            outcomes.Add(new StoreErasureOutcome(
                "conversations that could not be read", StoreErasureStatus.Failed, 0, UnreadableReason));
        }

        // Cleared only when every store that reads these ids succeeded, so nothing they lead to is left that only they
        // reach. Something kept partly needs no extra rule: a store narrowed by it reports that on its own line, a planned
        // task still running keeps its plan (the next request lists it again), and an unreadable conversation's id was never
        // recorded. A request whose own record failed held its leads back, so its stores did not all succeed either.
        var journalCleared = EveryEraserSucceeded(outcomes) && await ClearJournalAsync(ownerId, requestId);
        NoteJournalKept(journalRecorded && !journalCleared, subject, outcomes);

        await ScrubAsync(outcomes);

        outcomes.AddRange(_retained
            .Select(r => r.ToOutcome())
            .OfType<StoreErasureOutcome>()
            .DistinctBy(o => o.Store, StringComparer.Ordinal));

        return Fold(graphReceipt, outcomes);
    }

    /// <summary>
    /// The subject's conversation ids, through the store's id-only erasure listing when it has one (#907), which loads no
    /// transcripts and counts the records it cannot read instead of skipping them. A store without one is listed the
    /// ordinary way.
    /// </summary>
    private async Task<ConversationIdListing> ListConversationIdsAsync(string ownerId, CancellationToken cancellationToken) =>
        _conversations switch
        {
            null => ConversationIdListing.Empty,
            IConversationIdSource source => await source.ListConversationIdsAsync(ownerId, cancellationToken),
            var store => new ConversationIdListing(
                [.. (await store.ListAsync(ownerId, cancellationToken)).Select(c => c.Id)], 0),
        };

    /// <summary>
    /// The conversations erased are the ones listed before anything was deleted, so the stores reached through them are
    /// exactly the ones already cleared. A conversation the person started while the request ran was not among them;
    /// it and what it produced are kept, and the receipt says to run erasure again rather than claiming the person is gone.
    /// </summary>
    private async Task NoteConversationsStartedMeanwhileAsync(string ownerId, List<StoreErasureOutcome> outcomes)
    {
        if (_conversations is null)
            return;

        int remaining;
        try
        {
            remaining = (await ListConversationIdsAsync(ownerId, CancellationToken.None)).Ids.Count;
        }
        catch (Exception ex)
        {
            // The deletes are done; losing the receipt over this check would hide them.
            _logger.LogError(MessageWithheldException.From(ex), "Erasure could not check for conversations started meanwhile.");
            outcomes.Add(new StoreErasureOutcome(
                "conversations started during erasure", StoreErasureStatus.Failed, 0, FailedReason));
            return;
        }

        if (remaining > 0)
        {
            outcomes.Add(new StoreErasureOutcome(
                "conversations started during erasure", StoreErasureStatus.Skipped, 0, StartedMeanwhileReason));
        }
    }

    /// <summary>
    /// The same rule for planned tasks (#906), read before the stage after. The plan store erased only the runs it listed
    /// that had finished, and kept a plan with a run in progress or made since, so the next request still finds it; the
    /// receipt says so. Returns what is still recorded, or <see langword="null"/> when it could not be read, in which case
    /// the planned tasks are treated as not gone.
    /// </summary>
    private async Task<PlanRunTraces?> PlannedTasksStillRecordedAsync(string ownerId, List<StoreErasureOutcome> outcomes)
    {
        if (_planRuns is null)
            return PlanRunTraces.Empty;

        PlanRunTraces remaining;
        try
        {
            remaining = await _planRuns.ListRunTracesAsync(ownerId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(MessageWithheldException.From(ex), "Erasure could not check for planned tasks run meanwhile.");
            outcomes.Add(new StoreErasureOutcome("planned tasks run during erasure", StoreErasureStatus.Failed, 0, FailedReason));
            return null;
        }

        if (!remaining.IsEmpty)
        {
            outcomes.Add(new StoreErasureOutcome(
                "planned tasks run during erasure", StoreErasureStatus.Skipped, 0, RunMeanwhileReason));
        }

        return remaining;
    }

    /// <summary>
    /// Clears the deleted bytes every store left in its files, once every stage has run. Kept apart from the deletes: a
    /// delete is committed before its bytes can be cleared, so a clear that fails must not mark that store as failed and
    /// hold back the stages after it. It runs on every request, including one that found nothing to delete, so a retry
    /// clears what an earlier request could not. A failure is its own line and makes the receipt <c>Partial</c>.
    /// </summary>
    private async Task ScrubAsync(List<StoreErasureOutcome> outcomes)
    {
        foreach (var eraser in _erasers)
        {
            if (eraser is not IPersonalDataScrubber scrubber)
                continue;

            try
            {
                await scrubber.ScrubAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                var store = $"{eraser.ErasureStoreName} (deleted bytes)";
                _logger.LogError(MessageWithheldException.From(ex), "Erasure failed in store {Store}.", store);
                outcomes.Add(new StoreErasureOutcome(store, StoreErasureStatus.Failed, 0, ScrubFailedReason));
            }
        }
    }

    /// <summary>
    /// Runs <see cref="ErasureStage.AfterConversations"/> for <paramref name="narrowed"/>: the ids of whichever lead (the
    /// conversations, the plans) is gone, less what the plan store still records. When nothing is left out it is an
    /// ordinary stage. When something is (<paramref name="partly"/>), it runs only if the narrowed ids name anything; each
    /// line then says the rest was kept, since the store still holds what the surviving lead leads to and a retry finds it
    /// there, and a store that erases the whole person is held back, since it cannot be narrowed.
    /// </summary>
    private async Task RunStageAfterAsync(
        ErasureSubject narrowed, bool beforeSucceeded, bool partly, List<StoreErasureOutcome> outcomes)
    {
        var mayRun = beforeSucceeded && (!partly || narrowed.AllConversationIds.Count > 0 || !narrowed.PlanRuns.IsEmpty);
        foreach (var eraser in _erasers.Where(e => e.ErasureStage == ErasureStage.AfterConversations))
        {
            if (!mayRun || (partly && eraser.ErasureReach == ErasureReach.Owner))
            {
                outcomes.Add(new StoreErasureOutcome(eraser.ErasureStoreName, StoreErasureStatus.Skipped, 0, HeldBackReason));
                continue;
            }

            var outcome = await EraseAsync(eraser, narrowed, CancellationToken.None);
            outcomes.Add(partly && outcome.Status == StoreErasureStatus.Erased
                ? outcome with { Status = StoreErasureStatus.Skipped, Reason = PartlyHeldBackReason }
                : outcome);
        }
    }

    /// <summary>
    /// Runs one later stage when <paramref name="mayRun"/>; otherwise records each of its stores as held back, so a retry
    /// still finds them. Returns whether every store in the stage ran and succeeded.
    /// </summary>
    private async Task<bool> RunStageAsync(
        ErasureStage stage, bool mayRun, List<StoreErasureOutcome> outcomes, ErasureSubject subject,
        CancellationToken cancellationToken)
    {
        var allErased = mayRun;
        foreach (var eraser in _erasers.Where(e => e.ErasureStage == stage))
        {
            var outcome = mayRun
                ? await EraseAsync(eraser, subject, cancellationToken)
                : new StoreErasureOutcome(eraser.ErasureStoreName, StoreErasureStatus.Skipped, 0, HeldBackReason);
            allErased &= outcome.Status == StoreErasureStatus.Erased;
            outcomes.Add(outcome);
        }

        return allErased;
    }

    /// <summary>
    /// Erases the knowledge graph. Its own receipt is the base of the combined one; when it throws, an empty base is
    /// used and the failure is listed as a store line, so the other stores are still erased.
    /// </summary>
    private async Task<(ErasureReceipt Receipt, StoreErasureOutcome? Failure)> EraseGraphAsync(
        string ownerId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        try
        {
            return (await _graph.EraseByOwnerAsync(ownerId, cancellationToken), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(MessageWithheldException.From(ex), "Erasure failed in store {Store}.", GraphStoreName);
            var empty = new ErasureReceipt
            {
                RequestId = Guid.NewGuid().ToString(),
                ScopeId = ownerId,
                RequestedAt = requestedAt,
                CompletedAt = requestedAt,
                NodesDeleted = 0,
                EdgesDeleted = 0,
                FeedbackWeightsDeleted = 0,
                VectorEmbeddingsDeleted = 0,
            };
            return (empty, new StoreErasureOutcome(GraphStoreName, StoreErasureStatus.Failed, 0, FailedReason));
        }
    }

    private async Task<StoreErasureOutcome> EraseAsync(
        IPersonalDataEraser eraser, ErasureSubject subject, CancellationToken cancellationToken)
    {
        if (eraser is NonErasableStore)
            return new StoreErasureOutcome(eraser.ErasureStoreName, StoreErasureStatus.NotSupported, 0, NonErasableStore.Reason);

        try
        {
            return StoreErasureOutcome.Erased(
                eraser.ErasureStoreName, await eraser.EraseSubjectAsync(subject, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(MessageWithheldException.From(ex), "Erasure failed in store {Store}.", eraser.ErasureStoreName);
            return new StoreErasureOutcome(eraser.ErasureStoreName, StoreErasureStatus.Failed, 0, FailedReason);
        }
    }

    private ErasureReceipt Fold(ErasureReceipt graphReceipt, IReadOnlyList<StoreErasureOutcome> outcomes)
    {
        var reasons = new List<string>();
        if (graphReceipt.Completeness == ErasureCompleteness.Partial && graphReceipt.CompletenessReason is { } graphReason)
            reasons.Add(graphReason);

        reasons.AddRange(outcomes
            .Where(o => LeavesDataBehind(o.Status))
            .Select(o => $"{o.Store}: {o.Reason}"));

        return graphReceipt with
        {
            CompletedAt = _timeProvider.GetUtcNow(),
            Stores = outcomes,
            Completeness = reasons.Count == 0 ? ErasureCompleteness.Full : ErasureCompleteness.Partial,
            CompletenessReason = reasons.Count == 0 ? null : string.Join("; ", reasons),
        };
    }

    /// <summary>
    /// Whether a line means the subject's data may remain with no end date. A store whose records age out, an audit
    /// trail kept on purpose, or the organisation's own business content, is listed on the receipt without making it
    /// <c>Partial</c>.
    /// </summary>
    private static bool LeavesDataBehind(StoreErasureStatus status) => status switch
    {
        StoreErasureStatus.Erased => false,
        StoreErasureStatus.RetainedUntilExpiry => false,
        StoreErasureStatus.RetainedByObligation => false,
        StoreErasureStatus.RetainedAsBusinessContent => false,
        StoreErasureStatus.Failed => true,
        StoreErasureStatus.Skipped => true,
        StoreErasureStatus.NotSupported => true,
        StoreErasureStatus.RetainedIndefinitely => true,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Every erasure status must say whether it leaves data behind."),
    };
}

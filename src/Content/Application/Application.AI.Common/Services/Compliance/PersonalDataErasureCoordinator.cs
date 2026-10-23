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
/// <strong>Order.</strong> The subject's conversation ids are read first, before anything is deleted, because several
/// stores carry only a conversation id. Then the knowledge graph and every <see cref="ErasureStage.BeforeConversations"/>
/// store run. The conversations are erased next, only when every earlier store succeeded: deleting them after a failure
/// would leave the failed store's data with nothing left that leads to it. <see cref="ErasureStage.AfterConversations"/>
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
public sealed class PersonalDataErasureCoordinator : IPersonalDataErasure
{
    private const string GraphStoreName = "knowledge graph";
    private const string FailedReason = "the store could not be erased; the failure has been logged";
    private const string StartedMeanwhileReason =
        "the person started a conversation while erasure ran; it and what it produced were kept, so run erasure again";
    private const string ScrubFailedReason =
        "the records were deleted, but their bytes could not yet be cleared from the store's files; the failure has been logged, so run erasure again";
    private const string HeldBackReason =
        "kept because an earlier store failed; erasing it now would leave that store's data with nothing that leads to it, or re-arm a once-only tool in a surviving conversation";

    private readonly IErasureOrchestrator _graph;
    private readonly IConversationStore? _conversations;
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
    public PersonalDataErasureCoordinator(
        IErasureOrchestrator graph,
        IEnumerable<IPersonalDataEraser> erasers,
        IEnumerable<RetainedPersonalData> retained,
        IAmbientRequestScope ambientScope,
        TimeProvider timeProvider,
        ILogger<PersonalDataErasureCoordinator> logger,
        IConversationStore? conversations = null)
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
    }

    /// <inheritdoc />
    public async Task<ErasureReceipt> EraseOwnerAsync(string ownerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        using var systemScoped = _ambientScope.SuspendScope();

        // Read before any deletion. A failure here erases nothing, which is the only safe answer: without the ids the
        // stores reached through conversations are unreachable, and the conversations must not be deleted either.
        var conversationIds = _conversations is null
            ? []
            : (await _conversations.ListAsync(ownerId, cancellationToken)).Select(c => c.Id).ToList();
        var subject = new ErasureSubject(ownerId, conversationIds);

        var requestedAt = _timeProvider.GetUtcNow();
        var (graphReceipt, graphOutcome) = await EraseGraphAsync(ownerId, requestedAt, cancellationToken);

        var outcomes = new List<StoreErasureOutcome>();
        foreach (var eraser in _erasers.Where(e => e.ErasureStage == ErasureStage.BeforeConversations))
            outcomes.Add(await EraseAsync(eraser, subject, cancellationToken));

        // The graph is not reached through conversations, so its failure does not hold them back.
        if (graphOutcome is not null)
            outcomes.Insert(0, graphOutcome);

        // A store that cannot erase at all will not succeed on a retry either, so it does not hold the conversations back.
        var beforeSucceeded = outcomes.All(o => o.Store == GraphStoreName
            || o.Status is StoreErasureStatus.Erased or StoreErasureStatus.NotSupported);

        // From here on, deletion is not interrupted by the caller going away: a request cancelled between the
        // conversations and the stage after them would leave that stage's records with nothing that leads to them.
        var conversationsErased = await RunStageAsync(ErasureStage.Conversations, beforeSucceeded, outcomes, subject, CancellationToken.None);

        // Safe only when the person's conversations are really gone: none existed, or a conversation eraser ran and
        // succeeded. A host whose conversation store cannot erase leaves them standing, and they keep their ledger rows.
        var conversationsGone = subject.ConversationIds.Count == 0
            || (conversationsErased && _erasers.Any(e => e.ErasureStage == ErasureStage.Conversations));
        await RunStageAsync(ErasureStage.AfterConversations, beforeSucceeded && conversationsGone, outcomes, subject, CancellationToken.None);

        if (conversationsErased)
            await NoteConversationsStartedMeanwhileAsync(ownerId, outcomes);

        await ScrubAsync(outcomes);

        outcomes.AddRange(_retained
            .Select(r => r.ToOutcome())
            .OfType<StoreErasureOutcome>()
            .DistinctBy(o => o.Store, StringComparer.Ordinal));

        return Fold(graphReceipt, outcomes);
    }

    /// <summary>
    /// The conversations erased are the ones listed before anything was deleted, so the stores reached through them are
    /// exactly the ones already cleared. A conversation the person started while the request ran was not among them;
    /// it and what it produced are kept, and the receipt says to run erasure again rather than claiming the person is gone.
    /// </summary>
    private async Task NoteConversationsStartedMeanwhileAsync(string ownerId, List<StoreErasureOutcome> outcomes)
    {
        if (_conversations is null)
            return;

        var remaining = (await _conversations.ListAsync(ownerId, CancellationToken.None)).Count;
        if (remaining > 0)
        {
            outcomes.Add(new StoreErasureOutcome(
                "conversations started during erasure", StoreErasureStatus.Skipped, 0, StartedMeanwhileReason));
        }
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
                _logger.LogError(ex, "Erasure failed in store {Store}.", store);
                outcomes.Add(new StoreErasureOutcome(store, StoreErasureStatus.Failed, 0, ScrubFailedReason));
            }
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
            _logger.LogError(ex, "Erasure failed in store {Store}.", GraphStoreName);
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
            _logger.LogError(ex, "Erasure failed in store {Store}.", eraser.ErasureStoreName);
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

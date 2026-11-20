using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Extensions;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.AI.Common.Services;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Observability.Models;
using Domain.AI.Telemetry.Conventions;
using MediatR;
using Microsoft.Extensions.AI;
using Application.AI.Common.Models.Conversations;
using Presentation.AgentHub.DTOs;

namespace Presentation.AgentHub.Services;

public sealed partial class ConversationOrchestrator
{
    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="turn"/> holding this conversation's turn lease, and hands it a token that
    /// is cancelled if the lease is lost as well as when the caller cancels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every lease-holding operation on this type does exactly this, including
    /// <see cref="ReassignAgentAsync"/> — so the acquire/link/release shape lives here rather than
    /// repeated at each call site, which is the part where a mistake is invisible until two turns, or
    /// a turn and a reassignment, have already interleaved.
    /// </para>
    /// <para>
    /// <strong>What it deliberately does not do is re-read the conversation.</strong> Everything a
    /// turn reads from the record is already read under the lease, inside
    /// <see cref="DispatchTurnAsync"/>, and it has to be read there rather than here: retry and edit
    /// truncate and append <em>after</em> the lease is taken, so a record read at this point would
    /// carry a message count the turn has since changed. <c>AgentName</c> is different: it
    /// <em>can</em> change mid-flight, via <see cref="ReassignAgentAsync"/>
    /// (<c>PATCH /conversations/{id}/agent</c>), so <see cref="DispatchTurnAsync"/> does not accept
    /// it as a parameter at all — every caller used to pass one captured before its own lease
    /// acquisition, which made the stale value one call site away from being used by mistake rather
    /// than structurally impossible to reach. It re-reads <c>AgentName</c> fresh once it holds the
    /// lease (<c>dispatchAgentName</c>) — computed
    /// first, before anything else in that method reads or reports on the agent — and uses it for the
    /// actual dispatch, for resolving which agent the conversation cache builds or serves, and for
    /// every telemetry tag, health-tracker call, and session-tracking call the rest of the method
    /// makes. That last part matters beyond metrics hygiene: <c>EnsureSessionTrackedAsync</c>'s
    /// agent name reaches <c>ConversationTelemetryRecorder.BeginAsync</c>, which persists it into the
    /// durable <c>sessions</c> row on a conversation's first turn — tagging that row with a pre-lease
    /// name would be real data corruption, not a metric quirk, so this is not treated as an
    /// acceptable residual gap the way the pre-lease read itself is. Since
    /// <see cref="ReassignAgentAsync"/> writes and evicts under this same lease, a turn that raced a
    /// reassignment for the lease and lost cannot dispatch to, report on, or re-cache the agent it was
    /// reassigned away from — all of that would require using a value read before the lease, which
    /// this method no longer does anywhere.
    /// </para>
    /// <para>
    /// The lost-lease translation is the reason this cannot simply pass the linked token along and
    /// stop there. <see cref="DispatchTurnAsync"/> reads a cancelled token as a client disconnect and
    /// says so in the log; without this, a lease taken by another host would be recorded as the user
    /// closing their browser. The filter checks the caller's token too, so a real disconnect that
    /// happens to race the loss is still reported as a disconnect.
    /// </para>
    /// </remarks>
    private async Task<T> WithTurnLeaseAsync<T>(
        string conversationId,
        Func<LeasedTurn, Task<T>> turn,
        CancellationToken ct)
    {
        await using var leased = await LeasedTurn.AcquireAsync(_turnLease, conversationId, ct);

        try
        {
            return await turn(leased);
        }
        catch (OperationCanceledException) when (leased.LeaseWasLost)
        {
            _logger.LogWarning(
                "Turn on conversation {ConversationId} stopped: another host took its lease.",
                conversationId);

            throw new InvalidOperationException(ConversationLeaseNotice.Message);
        }
    }

    /// <summary>A turn waiting to be dispatched, and what is written when it is admitted (#964).</summary>
    /// <param name="Text">The user message the model is sent.</param>
    /// <param name="NewMessageId">
    /// The id of the message stored on admission (a send or an edit), or <see langword="null"/> for a retry, which
    /// re-sends a stored message. Named to the client as removed when the turn is not admitted, since the client shows
    /// the message from the moment it is sent (#1036).
    /// </param>
    /// <param name="ReplacesFromId">The first stored message the turn replaces; its history stops before it.</param>
    /// <param name="RetriedMessageId">For a retry, the stored message it re-sends; a verdict against it removes it.</param>
    /// <param name="OnAdmitted">What admission writes: the message, and an edit's or retry's truncation.</param>
    /// <param name="OnHistoryTruncated">For a retry, how the client is told a rejection removed messages.</param>
    private sealed record PendingTurn(
        string Text,
        Guid? NewMessageId,
        Guid? ReplacesFromId,
        Guid? RetriedMessageId,
        Func<CancellationToken, Task> OnAdmitted,
        Func<HistoryTruncation, CancellationToken, Task>? OnHistoryTruncated = null);

    /// <summary>
    /// The user message a retry of <paramref name="assistantMessageId"/> re-sends: the one just before it, or the last
    /// message when the conversation does not hold that id. <see langword="null"/> when that is not a user message.
    /// </summary>
    private static ConversationMessage? PrecedingUserMessage(
        IReadOnlyList<ConversationMessage> messages, Guid assistantMessageId)
    {
        var index = -1;
        for (var i = 0; i < messages.Count && index < 0; i++)
        {
            if (messages[i].Id == assistantMessageId)
                index = i;
        }

        var candidate = index switch
        {
            -1 => messages.Count > 0 ? messages[^1] : null,
            0 => null,
            _ => messages[index - 1],
        };
        return candidate?.Role == MessageRole.User ? candidate : null;
    }

    private async Task<TurnOutcome> DispatchTurnAsync(
        string sessionKey, string conversationId, PendingTurn pending,
        string callerId, Func<string, CancellationToken, Task>? onChunk, LeasedTurn leased, CancellationToken ct)
    {
        // The agent to dispatch to is resolved HERE, fresh under the lease, rather than accepted as
        // a parameter a caller captured before the lease was ever contested -- a reassignment that
        // lands between that pre-lease read and this call's own lease acquisition
        // (ReassignAgentAsync evicts the cache under the SAME lease) would otherwise leave THIS turn
        // to rebuild and re-cache the agent it was reassigned away from, right after the eviction
        // that was supposed to prevent exactly that. Not accepting a caller-supplied name at all,
        // rather than accepting one and remembering not to use it, is what makes that stale value
        // structurally unreachable here instead of merely unused by convention.
        //
        // Used for telemetry (below) and session tracking too, not only the eventual dispatch --
        // EnsureSessionTrackedAsync's agentName reaches ConversationTelemetryRecorder.BeginAsync,
        // which persists it into the durable sessions row on a conversation's first turn. Tagging
        // that row with a pre-lease name would be real data corruption, not a cosmetic metric.
        var updatedRecord = await _conversationStore.GetAsync(conversationId, callerId, ct);
        if (updatedRecord is null)
        {
            // The conversation existed when the caller made its own pre-lease read (that read would
            // have thrown otherwise), and vanished in the narrow window between that and this lease
            // acquisition. Deletion doesn't hold this same lease at all (#758), so this is a real,
            // separate gap this method cannot close on its own -- but it is now an explicit failure
            // outcome, not a silent fallback to a value nothing has captured for this call at all.
            return await HandleTurnErrorAsync(conversationId, callerId,
                new InvalidOperationException("Conversation not found."), AgentTurnErrorKind.Internal, pending,
                admitted: false, ct);
        }

        var dispatchAgentName = updatedRecord.AgentName;

        Activity.Current?.SetTag("agent.conversation_id", conversationId);
        Activity.Current?.SetTag(AgentConventions.Name, dispatchAgentName);
        Activity.Current?.SetTag(UserConventions.UserId, callerId);
        Activity.Current?.AddBaggage("agent.conversation_id", conversationId);
        Activity.Current?.AddBaggage(UserConventions.UserId, callerId);

        var telemetry = await EnsureSessionTrackedAsync(
            sessionKey, conversationId, dispatchAgentName, callerId, updatedRecord, ct);

        // Numbered from the conversation's turn count, not its message count. A message count advances
        // by two per turn, so the same conversation produced a different sequence over this transport
        // than over the bundle path — in one key space, on one dashboard (issues #255, #280).
        var turnNumber = telemetry.NextTurnNumber;

        // Conversation-lifetime budget gate: if prior turns already exhausted the cumulative token
        // ceiling, decline this turn gracefully (no LLM dispatch, no cost) with an explanatory
        // assistant message rather than throwing or surfacing an error to the client. Ahead of the
        // history read, which a declined turn does not use.
        var budgetStatus = await _conversationBudget.GetStatusAsync(conversationId, ct);
        if (budgetStatus.IsExhausted)
            return BuildBudgetExhaustedOutcome(conversationId, dispatchAgentName, pending, updatedRecord.Messages.Count);

        // The messages BEFORE the one being sent: the turn handler adds that one itself (#785), and it is not stored
        // until admission (#964). An edit or retry stops before the part it replaces, which is still stored.
        var history = await ConversationDispatchHistory.ReadPriorAsync(
            _conversationStore, conversationId, callerId, _config.MaxHistoryMessages, pending.ReplacesFromId, _logger, ct);

        var obsSessionId = telemetry.SessionId;

        var command = new ExecuteAgentTurnCommand
        {
            AgentName = dispatchAgentName,
            UserMessage = pending.Text,
            ConversationHistory = ToMeaiHistory(history),
            ConversationId = conversationId,
            TurnNumber = turnNumber,
            DeploymentOverride = updatedRecord.Settings?.DeploymentName,
            Temperature = updatedRecord.Settings?.Temperature,
            SystemPromptOverride = updatedRecord.Settings?.SystemPromptOverride,
            ObservabilitySessionId = obsSessionId,
        };

        var (dispatched, failure, admitted) = await SendWithAdmissionAsync(
            command, pending, onChunk, dispatchAgentName, ct);
        if (failure is not null)
            return await HandleDispatchFailureAsync(
                conversationId, callerId, pending, dispatchAgentName, failure, admitted, ct);

        var result = dispatched!;

        // Before the outcome is looked at: a failed or cancelled turn still spent.
        await _conversationBudget.RecordTurnUsageAsync(conversationId, result, _logger);

        if (!result.Success)
        {
            // #780: into the session rollup; ahead of the cancelled branch, as the charge is.
            await RecordFailedTurnAsync(sessionKey, telemetry, result, leased);

            // A disconnect can also surface as a failed result: the handler catches the
            // cancellation internally and tags it Cancelled. Treat only that as routine —
            // keying on the kind (not ct.IsCancellationRequested) avoids reclassifying a
            // genuine failure that merely coincides with a client drop as a disconnect.
            if (result.ErrorKind == AgentTurnErrorKind.Cancelled)
            {
                _logger.LogInformation(
                    "Turn for conversation {ConversationId} aborted by client disconnect; not recorded as an error.",
                    conversationId);
                throw new OperationCanceledException(ct);
            }

            _healthTracker.RecordError(dispatchAgentName);
            return await HandleTurnErrorAsync(conversationId, callerId,
                new InvalidOperationException(result.Error ?? "Agent returned a failure result."),
                result.ErrorKind, pending, admitted, ct);
        }

        var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, dispatchAgentName);
        if (result.ToolsInvoked.Count > 0)
            OrchestrationMetrics.ToolCalls.Add(result.ToolsInvoked.Count, agentTag);

        _healthTracker.RecordSuccess(dispatchAgentName);

        var userTag = new KeyValuePair<string, object?>(UserConventions.UserId, callerId);
        var userAgentTag = new KeyValuePair<string, object?>(AgentConventions.Name, dispatchAgentName);
        UserActivityMetrics.Turns.Add(1, userTag, userAgentTag);

        await RecordTurnAsync(sessionKey, telemetry, result, ct);

        // Token deltas were already streamed to the caller during dispatch via the
        // ambient AgentTurnStreamSink. The final authoritative text rides TurnComplete.
        var assistantMessageId = Guid.NewGuid();
        var assistantMsg = new ConversationMessage(
            assistantMessageId, MessageRole.Assistant, result.Response, DateTimeOffset.UtcNow,
            ToolCalls: result.ToolCalls);
        await _conversationStore.AppendMessageAsync(conversationId, callerId, assistantMsg, ct);

        var finalRecord = await _conversationStore.GetAsync(conversationId, callerId, ct);
        var finalTurnNumber = finalRecord?.Messages.Count ?? turnNumber + 1;

        return new TurnOutcome
        {
            Success = true,
            Response = result.Response,
            AssistantMessageId = assistantMessageId,
            FinalTurnNumber = finalTurnNumber,
        };
    }

    /// <summary>
    /// Sends the turn with its admission attached, and reports whether it was admitted once the dispatch has stopped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The streaming sink and the admission both flow ambiently, so the command stays a pure data record; both are
    /// restored afterwards so a nested or later dispatch on this flow is unaffected.
    /// </para>
    /// <para>
    /// A failure is returned rather than thrown so the admission is closed first, whatever happened: the dispatch can
    /// stop waiting while the pipeline is still screening (a timeout, a disconnect), and a late admission must not store
    /// the message after this method has reported it unstored. See <see cref="AgentTurnAdmission.DispatchAsync{TResult}"/>.
    /// </para>
    /// <para>
    /// A hub turn is agent work in flight, so it sits on the same active-runs gauge as a bundle run and an AG-UI run,
    /// around the dispatch only: the budget-exhausted return never reaches a model.
    /// </para>
    /// </remarks>
    private async Task<AdmittedDispatch<AgentTurnResult>> SendWithAdmissionAsync(
        ExecuteAgentTurnCommand command, PendingTurn pending, Func<string, CancellationToken, Task>? onChunk,
        string agentName, CancellationToken ct)
    {
        var previousSink = AgentTurnStreamSink.Current;
        if (onChunk is not null)
            AgentTurnStreamSink.Current = new AgentTurnStreamSink(onChunk);

        var runTag = new TagList { { AgentConventions.Name, agentName } };
        OrchestrationMetrics.RunsActive.Add(1, runTag);
        try
        {
            return await new AgentTurnAdmission(pending.OnAdmitted).DispatchAsync(() => _mediator.Send(command, ct));
        }
        finally
        {
            OrchestrationMetrics.RunsActive.Add(-1, runTag);
            AgentTurnStreamSink.Current = previousSink;
        }
    }

    /// <summary>Turns a dispatch that threw into the outcome the caller sees, or rethrows a client disconnect.</summary>
    /// <remarks>
    /// What happened to the message follows from <paramref name="admitted"/>, not from the exception type (#964). A
    /// genuine timeout cancels a linked token and surfaces as <see cref="TimeoutException"/> with
    /// <paramref name="ct"/> uncancelled, so it is an error, not a disconnect.
    /// </remarks>
    private async Task<TurnOutcome> HandleDispatchFailureAsync(
        string conversationId, string callerId, PendingTurn pending, string agentName, Exception failure,
        bool admitted, CancellationToken ct)
    {
        if (failure is OperationCanceledException && ct.IsCancellationRequested)
        {
            // A routine client disconnect, not an agent error: no health error and no synthetic message. Nothing to
            // clean up either: a turn stopped before admission stored nothing.
            _logger.LogInformation(
                "Turn dispatch for conversation {ConversationId} cancelled by client disconnect.", conversationId);
            ExceptionDispatchInfo.Throw(failure);
        }

        // A rejection is not the agent failing, so it is not counted against its health, and adds no error row.
        if (RejectedTurnMessage.IsRejection(failure))
            return await RejectTurnAsync(conversationId, callerId, pending, failure, admitted);

        _healthTracker.RecordError(agentName);
        var kind = failure is AiProviderNotConfiguredException ? AgentTurnErrorKind.Configuration : AgentTurnErrorKind.Internal;
        return await HandleTurnErrorAsync(conversationId, callerId, failure, kind, pending, admitted, ct);
    }

    /// <summary>The outcome of a rejected turn, and the retried message a verdict removes.</summary>
    /// <remarks>
    /// A rejection after admission (an output-side block a consumer raises from inside the turn) leaves the message: it
    /// was admitted, and the model may already have answered it. Before admission a send or an edit stored nothing,
    /// and the client is told to drop the message it shows. A retry re-sends a stored message, which a verdict removes
    /// with the answer after it; the client is told through the same truncation notice a retry already uses.
    /// </remarks>
    private async Task<TurnOutcome> RejectTurnAsync(
        string conversationId, string callerId, PendingTurn pending, Exception rejection, bool admitted)
    {
        _logger.LogWarning(rejection, "Turn on conversation {ConversationId} rejected.", conversationId);
        var clientMessage = RejectedTurnMessage.ClientMessageFor(rejection);

        if (admitted)
            return new TurnOutcome { Success = false, ErrorMessage = clientMessage };

        if (pending.RetriedMessageId is not { } retried)
            return new TurnOutcome { Success = false, ErrorMessage = clientMessage, RemovedMessageId = pending.NewMessageId };

        if (!RejectedTurnMessage.RemovesRetriedMessage(rejection))
            return new TurnOutcome { Success = false, ErrorMessage = clientMessage };

        var remaining = await RejectedTurnMessage.RemoveRetriedMessageAsync(
            _conversationStore, conversationId, callerId, retried, _logger);
        if (remaining is null)
            return new TurnOutcome { Success = false, ErrorMessage = clientMessage };

        await SignalHistoryTruncatedAsync(
            pending.OnHistoryTruncated, new HistoryTruncation(retried, remaining.Messages.Count));
        return new TurnOutcome
        {
            Success = false,
            ErrorMessage = clientMessage,
            RemovedMessageId = retried,
            HistoryKeepCount = remaining.Messages.Count,
        };
    }

    /// <summary>
    /// Finds where this conversation has got to, and keeps the connection tracker in step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session id and the running totals come from <see cref="IConversationTelemetryRecorder"/>,
    /// which reads them off the conversation. They used to be opened fresh here on every conversation
    /// switch and accumulated on a per-<em>connection</em> object — so reconnecting restamped the
    /// session's start time and then overwrote the conversation's rollup with whatever the new
    /// connection had spent, which is nothing (issue #280).
    /// </para>
    /// <para>
    /// The connection tracker stays, because it answers a different question: which conversation this
    /// connection is on, for idle cleanup and the active-conversation view. It is no longer the source
    /// of truth for what the conversation has spent.
    /// </para>
    /// </remarks>
    private async Task<ConversationTelemetryState> EnsureSessionTrackedAsync(
        string sessionKey, string conversationId, string agentName, string callerId,
        ConversationRecord? knownRecord, CancellationToken ct)
    {
        var tracked = _connectionTracker.Get(sessionKey);

        // A connection moving to a different conversation still ends the one it is leaving, exactly as
        // before. It is tempting not to — the conversation is not over, this connection just stopped
        // looking at it — but nothing else would ever end it: the disconnect and idle-cleanup paths both
        // end whatever the tracker currently holds, which by then is the NEW conversation. Dropping this
        // would leave the old session `running` forever, which is worse than ending it early.
        //
        // What it costs, stated because the recorder now adopts rather than restarts: coming back to
        // that conversation writes further turns into a row already marked completed. That is the
        // session lifetime being per-connection while the session row is per-conversation, which is a
        // design gap this change surfaces rather than creates — tracked separately.

        // The conversation this connection is leaving, or null when it is not leaving one. Held as the
        // entry rather than a bool so both uses below read it off the same non-null reference — the
        // bool version needed `tracked!` at each use, which asserts a fact the compiler could not see
        // and the second use is fifty lines from the check that establishes it.
        var leaving = tracked is not null && tracked.ConversationId != conversationId ? tracked : null;
        if (leaving is not null)
        {
            await _observabilityStore.EndSessionAsync(
                leaving.ObservabilitySessionId, SessionStatus.Completed, cancellationToken: ct);
        }

        // knownRecord is the SAME record DispatchTurnAsync already fetched (fresh, under the lease)
        // to compute dispatchAgentName -- passing it through here avoids a second, identical
        // IConversationStore.GetAsync round-trip BeginAsync's LoadAsync fallback would otherwise
        // make on every single turn.
        var state = await _telemetryRecorder.BeginAsync(
            conversationId, callerId, agentName, knownRecord, ct);

        // Debug, not warning: an empty id is what a host running without an observability database gets
        // on every turn, and that is a supported configuration. At warning level this filled the log of
        // every such deployment with a line about a feature it had chosen not to switch on.
        if (state.SessionId == Guid.Empty)
            _logger.LogDebug("No observability session for conversation {ConversationId}", conversationId);

        if (tracked?.ConversationId == conversationId)
        {
            // #765: the connection stayed on this conversation, but the conversation may have been
            // reassigned to a different agent since this connection was last tracked (PATCH
            // /conversations/{id}/agent). dispatchAgentName (the caller's `agentName`, resolved
            // fresh under the turn lease) is the source of truth; refresh the tracked entry so
            // ConnectionsActive/ConversationDuration/TurnsPerConversation at disconnect or idle
            // cleanup — and the active-conversation view — report the agent the conversation
            // actually belongs to now, not whatever it was first tracked under.
            //
            // OrdinalIgnoreCase, matching ReassignAgentAsync's own no-op check
            // (ConversationOrchestrator.ReassignAgent.cs) — a pure casing change is a no-op there
            // and must not be treated as a real reassignment here, or one agent's metrics would
            // fragment across casing variants for no reason.
            if (!string.Equals(tracked.AgentName, agentName, StringComparison.OrdinalIgnoreCase))
            {
                // The gauge moves with the entry it describes, exactly like the leaving-conversation
                // case below: decrement the agent this connection is leaving, increment the one it is
                // joining, both together and synchronously (no await between them or before the
                // Track below) — this connection was already counted as active, it is only the AGENT
                // tag that changed, so skipping either half would leave the counter permanently off
                // by one for that agent, the identical defect the switch case's own comment (below)
                // exists to avoid.
                AdjustConnectionsActive(-1, tracked.AgentName);
                AdjustConnectionsActive(1, agentName);

                _connectionTracker.Track(sessionKey, tracked with { AgentName = agentName });
            }

            return state;
        }

        // The gauge counts entries in the tracker, so it moves where entries move — here, next to the
        // Track that replaces one, and not a moment earlier. Decrementing up beside EndSessionAsync
        // reads more naturally and is wrong: BeginAsync above can throw (a cancelled token as the user
        // navigates away, a store that refuses the new conversation), and then Track never runs, the
        // tracker still holds the OLD entry, and the disconnect that eventually arrives decrements it a
        // second time. Two decrements for one increment, on an up-down counter that never recovers —
        // the exact defect this split exists to remove, reintroduced by the split.
        if (leaving is not null)
        {
            AdjustConnectionsActive(-1, leaving.AgentName);
        }

        _connectionTracker.Track(sessionKey, new ActiveConversationInfo(
            conversationId, agentName, callerId, DateTimeOffset.UtcNow,
            state.Totals.TurnCount, state.SessionId,
            state.Totals.InputTokens, state.Totals.OutputTokens,
            state.Totals.CacheRead, state.Totals.CacheWrite,
            state.Totals.CostUsd, state.Totals.ToolCallCount));

        // A connection, not a session and not a conversation: this is the moment one starts watching a
        // conversation, and every decrement is a moment one stops.
        AdjustConnectionsActive(1, agentName);
        return state;
    }

    /// <summary>
    /// Moves the <c>ConnectionsActive</c> gauge by <paramref name="delta"/> for one agent tag — the
    /// single place that shape is spelled out, after three independent review passes on #765 flagged
    /// it as hand-copied at every call site above.
    /// </summary>
    private static void AdjustConnectionsActive(int delta, string agentName)
        => OrchestrationMetrics.ConnectionsActive.Add(delta, new TagList { { AgentConventions.Name, agentName } });

    /// <summary>
    /// Records the turn against the conversation, and mirrors the new totals onto the connection view.
    /// </summary>
    /// <remarks>
    /// The write itself belongs to the shared recorder — including the cache hit rate, which this path
    /// used to compute with a different denominator than the other two transports, so the same column
    /// meant different things depending on how the conversation was reached.
    /// </remarks>
    private async Task<ConversationTelemetryState> RecordTurnAsync(
        string sessionKey, ConversationTelemetryState state, AgentTurnResult result, CancellationToken ct)
    {
        var updated = await _telemetryRecorder.RecordTurnAsync(state, result.ToTurnTelemetry(), ct);
        MirrorTotalsOntoConnection(sessionKey, updated);
        return updated;
    }

    /// <summary>
    /// The failed or cancelled counterpart of <see cref="RecordTurnAsync"/> (#780): adds the turn to the
    /// conversation's totals when it spent anything, and mirrors them onto the connection view.
    /// </summary>
    private async Task RecordFailedTurnAsync(
        string sessionKey, ConversationTelemetryState state, AgentTurnResult result, LeasedTurn leased) =>
        MirrorTotalsOntoConnection(
            sessionKey, await _telemetryRecorder.RecordFailedTurnAsync(state, result.ToTurnTelemetry(), leased));

    private void MirrorTotalsOntoConnection(string sessionKey, ConversationTelemetryState updated)
    {
        if (_connectionTracker.Get(sessionKey) is not { } convInfo)
            return;

        _connectionTracker.Track(sessionKey, convInfo with
        {
            LastActivityAt = DateTimeOffset.UtcNow,
            TurnCount = updated.Totals.TurnCount,
            ToolCallCount = updated.Totals.ToolCallCount,
            TotalInputTokens = updated.Totals.InputTokens,
            TotalOutputTokens = updated.Totals.OutputTokens,
            TotalCacheRead = updated.Totals.CacheRead,
            TotalCacheWrite = updated.Totals.CacheWrite,
            TotalCostUsd = updated.Totals.CostUsd,
        });
    }

    /// <summary>
    /// Logs a failed turn and builds what the caller is told. An admitted turn gets an error row after its message; a
    /// turn that was not admitted left the conversation as it was, so nothing is added and the client is told to drop
    /// the message it shows (#964).
    /// </summary>
    private async Task<TurnOutcome> HandleTurnErrorAsync(
        string conversationId, string callerId, Exception ex, AgentTurnErrorKind errorKind, PendingTurn pending,
        bool admitted, CancellationToken ct)
    {
        _logger.LogError(ex, "Agent turn failed for conversation {ConversationId}.", conversationId);

        // A provider-configuration failure carries an actionable, secret-free message. Surface it in
        // Development so the chat explains what to fix; keep it generic in Production to avoid leaking
        // configuration detail. Mirrors AgUiRunHandler so both transports behave the same.
        var clientMessage = errorKind == AgentTurnErrorKind.Configuration
            && _environment.IsDevelopment()
            && !string.IsNullOrWhiteSpace(ex.Message)
                ? ex.Message
                : "An error occurred processing your request.";

        if (!admitted)
            return new TurnOutcome { Success = false, ErrorMessage = clientMessage, RemovedMessageId = pending.NewMessageId };

        var errorRow = new ConversationMessage(
            Guid.NewGuid(),
            MessageRole.Assistant,
            "[Error] The agent encountered an error.",
            DateTimeOffset.UtcNow);
        try
        {
            await _conversationStore.AppendMessageAsync(conversationId, callerId, errorRow, ct);
        }
        catch (Exception storeEx)
        {
            // Not stored, so the client is not told about it either: both sides keep the same messages (#961).
            _logger.LogError(storeEx, "Failed to append error message to conversation {ConversationId}.", conversationId);
            return new TurnOutcome { Success = false, ErrorMessage = clientMessage };
        }

        return new TurnOutcome
        {
            Success = false,
            ErrorMessage = clientMessage,
            ErrorRow = errorRow,
        };
    }

    /// <summary>
    /// Builds the graceful outcome for a turn declined because the conversation exhausted its lifetime token budget:
    /// records the metric, and returns a successful outcome flagged <see cref="TurnOutcome.BudgetExhausted"/> so the
    /// client can surface it (e.g. disable further input) without treating it as an error. No LLM is dispatched.
    /// </summary>
    /// <remarks>
    /// Nothing is stored. The turn was never admitted, so its message was never screened, and storing it would let a
    /// caller queue unscreened text that replays as history once the budget comes back (#964). The notice is not stored
    /// either: the conversation is left as it was, and the client is told to drop the message it shows.
    /// </remarks>
    private TurnOutcome BuildBudgetExhaustedOutcome(
        string conversationId, string agentName, PendingTurn pending, int messageCount)
    {
        _logger.LogWarning(
            "Conversation {ConversationId} declined a turn: lifetime token budget exhausted", conversationId);
        OrchestrationMetrics.ConversationsBudgetStopped.Add(
            1, new KeyValuePair<string, object?>(AgentConventions.Name, agentName));

        return new TurnOutcome
        {
            Success = true,
            Response = ConversationBudgetNotice.Message,
            AssistantMessageId = Guid.NewGuid(),
            FinalTurnNumber = messageCount,
            BudgetExhausted = true,
            RemovedMessageId = pending.NewMessageId,
        };
    }

    // Read fresh on every call, not cached — states "live" (#515); see
    // ToolCallReplayWindowPolicy.FromCurrentSettings' remarks for why that's a deliberate choice here.
    private IReadOnlyList<ChatMessage> ToMeaiHistory(IReadOnlyList<ConversationMessage> messages) =>
        ConversationMessageMapping.ToChatMessages(
            messages, ToolCallReplayWindowPolicy.FromCurrentSettings(_toolCallReplayTreatment), _logger);
}

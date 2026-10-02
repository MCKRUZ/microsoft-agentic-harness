using System.Diagnostics;
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
        Func<CancellationToken, Task<T>> turn,
        CancellationToken ct)
    {
        await using var leased = await LeasedTurn.AcquireAsync(_turnLease, conversationId, ct);

        try
        {
            return await turn(leased.Token);
        }
        catch (OperationCanceledException) when (leased.LeaseWasLost)
        {
            _logger.LogWarning(
                "Turn on conversation {ConversationId} stopped: another host took its lease.",
                conversationId);

            throw new InvalidOperationException(ConversationLeaseNotice.Message);
        }
    }

    private async Task<TurnOutcome> DispatchTurnAsync(
        string sessionKey, string conversationId, string userMessage,
        string callerId, Func<string, CancellationToken, Task>? onChunk, CancellationToken ct)
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
                new InvalidOperationException("Conversation not found."), AgentTurnErrorKind.Internal, ct);
        }

        var dispatchAgentName = updatedRecord.AgentName;

        Activity.Current?.SetTag("agent.conversation_id", conversationId);
        Activity.Current?.SetTag(AgentConventions.Name, dispatchAgentName);
        Activity.Current?.SetTag(UserConventions.UserId, callerId);
        Activity.Current?.AddBaggage("agent.conversation_id", conversationId);
        Activity.Current?.AddBaggage(UserConventions.UserId, callerId);

        var telemetry = await EnsureSessionTrackedAsync(
            sessionKey, conversationId, dispatchAgentName, callerId, updatedRecord, ct);

        var history = await _conversationStore.GetHistoryForDispatch(
            conversationId, callerId, _config.MaxHistoryMessages, ct) ?? [];

        // Numbered from the conversation's turn count, not its message count. A message count advances
        // by two per turn, so the same conversation produced a different sequence over this transport
        // than over the bundle path — in one key space, on one dashboard (issues #255, #280).
        var turnNumber = telemetry.NextTurnNumber;

        // Conversation-lifetime budget gate: if prior turns already exhausted the cumulative token
        // ceiling, decline this turn gracefully (no LLM dispatch, no cost) with an explanatory
        // assistant message rather than throwing or surfacing an error to the client.
        var budgetStatus = await _conversationBudget.GetStatusAsync(conversationId, ct);
        if (budgetStatus.IsExhausted)
            return await BuildBudgetExhaustedOutcomeAsync(conversationId, callerId, dispatchAgentName, turnNumber, ct);

        var obsSessionId = telemetry.SessionId;

        var command = new ExecuteAgentTurnCommand
        {
            AgentName = dispatchAgentName,
            UserMessage = userMessage,
            ConversationHistory = ToMeaiHistory(history),
            ConversationId = conversationId,
            TurnNumber = turnNumber,
            DeploymentOverride = updatedRecord.Settings?.DeploymentName,
            Temperature = updatedRecord.Settings?.Temperature,
            SystemPromptOverride = updatedRecord.Settings?.SystemPromptOverride,
            ObservabilitySessionId = obsSessionId,
        };

        // Attach the streaming sink so the agent-turn handler streams real model token
        // deltas to the caller as they arrive. Flowing it ambiently (AsyncLocal) keeps the
        // MediatR command a pure data record. Restored in finally so nested/subsequent
        // dispatches on this async flow are unaffected.
        AgentTurnResult result;
        var previousSink = AgentTurnStreamSink.Current;
        if (onChunk is not null)
            AgentTurnStreamSink.Current = new AgentTurnStreamSink(onChunk);

        // A hub turn is agent work in flight, so it belongs on the same gauge as a bundle run and an
        // AG-UI run. Counting it only on those two would leave "Active Runs" reading zero on a
        // SignalR-only deployment while the agent is generating — the same defect the split was for,
        // pointing the other way. It sits around the dispatch rather than the whole method because the
        // budget-exhausted return above never reaches a model.
        var runTag = new TagList { { AgentConventions.Name, dispatchAgentName } };
        OrchestrationMetrics.RunsActive.Add(1, runTag);
        try
        {
            result = await _mediator.Send(command, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The connection token cancelled mid-turn: a routine client disconnect, not an
            // agent error. Abort without recording health errors or a synthetic message. A
            // genuine timeout cancels a linked token (surfaced as TimeoutException with `ct`
            // uncancelled) and falls through to the error handler below.
            _logger.LogInformation(
                "Turn dispatch for conversation {ConversationId} cancelled by client disconnect.", conversationId);
            throw;
        }
        catch (Exception ex)
        {
            _healthTracker.RecordError(dispatchAgentName);
            var kind = ex is AiProviderNotConfiguredException ? AgentTurnErrorKind.Configuration : AgentTurnErrorKind.Internal;
            return await HandleTurnErrorAsync(conversationId, callerId, ex, kind, ct);
        }
        finally
        {
            OrchestrationMetrics.RunsActive.Add(-1, runTag);
            AgentTurnStreamSink.Current = previousSink;
        }

        // Charged before the outcome is looked at: a turn that failed or was cancelled still paid for
        // the model calls it made, and the budget is what stops a conversation spending without limit.
        await _conversationBudget.RecordTurnUsageAsync(conversationId, result, _logger);

        if (!result.Success)
        {
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
                result.ErrorKind, ct);
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
        var updated = await _telemetryRecorder.RecordTurnAsync(
            state,
            new ConversationTurnTelemetry(
                result.InputTokens, result.OutputTokens, result.CacheRead, result.CacheWrite,
                result.CostUsd, result.ToolsInvoked.Count, result.Model),
            ct);

        if (_connectionTracker.Get(sessionKey) is { } convInfo)
        {
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

        return updated;
    }

    private async Task<TurnOutcome> HandleTurnErrorAsync(
        string conversationId, string callerId, Exception ex, AgentTurnErrorKind errorKind, CancellationToken ct)
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

        try
        {
            var errorMsg = new ConversationMessage(
                Guid.NewGuid(),
                MessageRole.Assistant,
                "[Error] The agent encountered an error.",
                DateTimeOffset.UtcNow);
            await _conversationStore.AppendMessageAsync(conversationId, callerId, errorMsg, ct);
        }
        catch (Exception storeEx)
        {
            _logger.LogError(storeEx, "Failed to append error message to conversation {ConversationId}.", conversationId);
        }

        return new TurnOutcome
        {
            Success = false,
            ErrorMessage = clientMessage,
        };
    }

    /// <summary>
    /// Builds the graceful outcome for a turn declined because the conversation exhausted its
    /// lifetime token budget: persists an explanatory assistant message, records the metric, and
    /// returns a successful outcome flagged <see cref="TurnOutcome.BudgetExhausted"/> so the client can
    /// surface it (e.g. disable further input) without treating it as an error. No LLM is dispatched.
    /// </summary>
    private async Task<TurnOutcome> BuildBudgetExhaustedOutcomeAsync(
        string conversationId, string callerId, string agentName, int turnNumber, CancellationToken ct)
    {
        var message = ConversationBudgetNotice.Message;

        _logger.LogWarning(
            "Conversation {ConversationId} declined a turn: lifetime token budget exhausted", conversationId);
        OrchestrationMetrics.ConversationsBudgetStopped.Add(
            1, new KeyValuePair<string, object?>(AgentConventions.Name, agentName));

        var assistantMessageId = Guid.NewGuid();
        var assistantMsg = new ConversationMessage(
            assistantMessageId, MessageRole.Assistant, message, DateTimeOffset.UtcNow);
        await _conversationStore.AppendMessageAsync(conversationId, callerId, assistantMsg, ct);

        var finalRecord = await _conversationStore.GetAsync(conversationId, callerId, ct);
        var finalTurnNumber = finalRecord?.Messages.Count ?? turnNumber + 1;

        return new TurnOutcome
        {
            Success = true,
            Response = message,
            AssistantMessageId = assistantMessageId,
            FinalTurnNumber = finalTurnNumber,
            BudgetExhausted = true,
        };
    }

    // Read fresh on every call, not cached — states "live" (#515); see
    // ToolCallReplayWindowPolicy.FromCurrentSettings' remarks for why that's a deliberate choice here.
    private IReadOnlyList<ChatMessage> ToMeaiHistory(IReadOnlyList<ConversationMessage> messages) =>
        ConversationMessageMapping.ToChatMessages(
            messages, ToolCallReplayWindowPolicy.FromCurrentSettings(_toolCallReplayTreatment), _logger);
}

using System.Diagnostics;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.AI.Common.Services;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Observability.Models;
using Domain.AI.Telemetry.Conventions;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Application.AI.Common.Models.Conversations;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Hubs;
using Presentation.AgentHub.Interfaces;

namespace Presentation.AgentHub.Services;

/// <summary>
/// Owns conversation lifecycle, turn orchestration, ownership validation, session
/// management, and metrics recording. Extracted from <see cref="AgentTelemetryHub"/>
/// to make the business logic testable without a SignalR transport.
/// </summary>
public sealed class ConversationOrchestrator : IConversationOrchestrator
{
    private readonly IMediator _mediator;
    private readonly IConversationStore _conversationStore;
    private readonly IConversationTurnLease _turnLease;
    private readonly ISessionHealthTracker _healthTracker;
    private readonly IObservabilityStore _observabilityStore;
    private readonly IConversationTelemetryRecorder _telemetryRecorder;
    private readonly IConnectionTracker _connectionTracker;
    private readonly IConversationBudgetTracker _conversationBudget;
    private readonly IToolCallReplayTreatment _toolCallReplayTreatment;
    private readonly AgentHubConfig _config;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ConversationOrchestrator> _logger;

    public ConversationOrchestrator(
        IMediator mediator,
        IConversationStore conversationStore,
        IConversationTurnLease turnLease,
        ISessionHealthTracker healthTracker,
        IObservabilityStore observabilityStore,
        IConversationTelemetryRecorder telemetryRecorder,
        IConnectionTracker connectionTracker,
        IConversationBudgetTracker conversationBudget,
        IToolCallReplayTreatment toolCallReplayTreatment,
        IOptions<AgentHubConfig> config,
        IHostEnvironment environment,
        ILogger<ConversationOrchestrator> logger)
    {
        _mediator = mediator;
        _conversationStore = conversationStore;
        _turnLease = turnLease;
        _healthTracker = healthTracker;
        _observabilityStore = observabilityStore;
        _telemetryRecorder = telemetryRecorder;
        _connectionTracker = connectionTracker;
        _conversationBudget = conversationBudget;
        _toolCallReplayTreatment = toolCallReplayTreatment;
        _config = config.Value;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<(ConversationRecord Record, IReadOnlyList<ConversationMessage> History)> StartConversationAsync(
        string sessionKey, string agentName, string? conversationId, string callerId, CancellationToken ct)
    {
        // The only entry point that may arrive without an id — "start me a fresh conversation". Every
        // other one takes a non-null id and reads through the store directly, which is where the
        // ownership refusal now comes from.
        //
        // A supplied id goes through the store's atomic open rather than the read-then-create this used
        // to compose. That composition is a transcript-destroying race, because CreateAsync REPLACES:
        // two clients reconnecting on the same id can both see it absent, and the loser's create
        // deletes the winner's turns. A freshly minted id cannot collide, so that branch still creates.
        ConversationRecord record;
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            record = await _conversationStore.CreateAsync(agentName, callerId, conversationId: null, ct: ct);

            _logger.LogInformation("Created conversation {ConversationId} for user {UserId}.",
                record.Id, callerId);
        }
        else
        {
            record = await _conversationStore.GetOrCreateAsync(agentName, callerId, conversationId, ct);
        }

        var history = await _conversationStore.GetHistoryForDispatch(
            record.Id, callerId, _config.MaxHistoryMessages, ct) ?? [];

        return (record, history);
    }

    /// <inheritdoc />
    public async Task SetSettingsAsync(
        string conversationId, ConversationSettings settings, string callerId, CancellationToken ct)
    {
        // No ownership pre-read: the update refuses a conversation the caller does not own, and
        // answers null for one that does not exist — the two outcomes the pre-read used to produce.
        var updated = await _conversationStore.UpdateSettingsAsync(conversationId, callerId, settings, ct)
            ?? throw new InvalidOperationException("Conversation not found.");

        _logger.LogInformation(
            "Updated conversation {ConversationId} settings (deployment={Deployment}, temperature={Temperature}, promptOverride={HasPrompt}).",
            updated.Id,
            settings.DeploymentName ?? "(default)",
            settings.Temperature?.ToString("0.##") ?? "(default)",
            !string.IsNullOrEmpty(settings.SystemPromptOverride));
    }

    /// <inheritdoc />
    public async Task<TurnOutcome> SendMessageAsync(
        string sessionKey, string conversationId, Guid userMessageId, string message, string callerId,
        Func<string, CancellationToken, Task>? onChunk, CancellationToken ct)
    {
        var record = await _conversationStore.GetAsync(conversationId, callerId, ct)
            ?? throw new InvalidOperationException("Conversation not found.");

        return await WithTurnLeaseAsync(conversationId, async turnCt =>
        {
            var userMsg = new ConversationMessage(
                userMessageId == Guid.Empty ? Guid.NewGuid() : userMessageId,
                MessageRole.User, message, DateTimeOffset.UtcNow);
            await _conversationStore.AppendMessageAsync(conversationId, callerId, userMsg, turnCt);

            return await DispatchTurnAsync(
                sessionKey, conversationId, record.AgentName, message, callerId, onChunk, turnCt);
        }, ct);
    }

    /// <summary>
    /// Invokes <paramref name="onHistoryTruncated"/>, if provided, with the surviving message
    /// count — swallowing (and logging) any exception it throws, EXCEPT a cancellation matching
    /// <paramref name="ct"/> itself, which is let through rather than swallowed.
    /// </summary>
    /// <remarks>
    /// The truncation this signals has already committed durably in <see cref="_conversationStore"/>
    /// by the time this runs. A generic transport failure delivering the notice (a slow client, a
    /// transient send error) must not abort the turn that follows — the caller already dispatched
    /// the store mutation that made this notice worth sending, so treating the notice itself as
    /// best-effort is what keeps that kind of hiccup from turning a durable truncation into a turn
    /// that never dispatches and a user message that silently vanishes.
    /// <para>
    /// A cancellation is different: it means the client's own connection is gone, not that the send
    /// merely failed. There is no one left to stream deltas to, so letting it propagate and abort —
    /// rather than swallowing it and dispatching a turn for a vanished client — is the routine
    /// disconnect handling this codebase already uses elsewhere (see <c>DispatchTurnAsync</c>'s
    /// <c>OperationCanceledException when (ct.IsCancellationRequested)</c> handling).
    /// </para>
    /// See #328 (why this fires before dispatch, not after) and its follow-up hardening (this
    /// swallow, and the ordering of this call relative to any local mutation that must NOT be
    /// reported as done before it actually is).
    /// </remarks>
    private async Task SignalHistoryTruncatedAsync(
        Func<int, CancellationToken, Task>? onHistoryTruncated, int keepCount, CancellationToken ct)
    {
        if (onHistoryTruncated is null) return;

        try
        {
            await onHistoryTruncated(keepCount, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The filter checks ct.IsCancellationRequested, not just the exception's type: an
            // OperationCanceledException from some OTHER token (an internal timeout, not this
            // turn's own connection going away) must still be swallowed like any other transient
            // failure — only a cancellation that actually matches ct signals a real disconnect.
            _logger.LogWarning(ex,
                "Failed to notify the client of a history truncation to {KeepCount} messages — " +
                "the truncation itself already committed and the turn continues.", keepCount);
        }
    }

    /// <inheritdoc />
    public async Task<TurnOutcome> RetryFromMessageAsync(
        string sessionKey, string conversationId, Guid assistantMessageId, string callerId,
        Func<string, CancellationToken, Task>? onChunk, CancellationToken ct,
        Func<int, CancellationToken, Task>? onHistoryTruncated = null)
    {
        var record = await _conversationStore.GetAsync(conversationId, callerId, ct)
            ?? throw new InvalidOperationException("Conversation not found.");

        return await WithTurnLeaseAsync(conversationId, async turnCt =>
        {
            var truncated = await _conversationStore.TruncateFromMessageAsync(
                    conversationId, callerId, assistantMessageId, turnCt)
                ?? throw new InvalidOperationException("Conversation not found.");

            var last = truncated.Messages.LastOrDefault();
            if (last is null || last.Role != MessageRole.User)
                throw new InvalidOperationException("Cannot retry: no preceding user message found.");

            // Signal the truncation BEFORE dispatching — see the interface's remarks (#328): a
            // caller streaming onChunk must be able to tell its client to drop the stale local
            // tail before this turn's own deltas arrive, not after the whole turn completes.
            await SignalHistoryTruncatedAsync(onHistoryTruncated, truncated.Messages.Count, turnCt);

            var outcome = await DispatchTurnAsync(
                sessionKey, conversationId, record.AgentName, last.Content, callerId, onChunk, turnCt);

            return outcome with { HistoryKeepCount = truncated.Messages.Count };
        }, ct);
    }

    /// <inheritdoc />
    public async Task<TurnOutcome> EditAndResubmitAsync(
        string sessionKey, string conversationId, Guid userMessageId, Guid newUserMessageId,
        string newContent, string callerId,
        Func<string, CancellationToken, Task>? onChunk, CancellationToken ct,
        Func<int, CancellationToken, Task>? onHistoryTruncated = null)
    {
        var record = await _conversationStore.GetAsync(conversationId, callerId, ct)
            ?? throw new InvalidOperationException("Conversation not found.");

        return await WithTurnLeaseAsync(conversationId, async turnCt =>
        {
            var truncated = await _conversationStore.TruncateFromMessageAsync(
                    conversationId, callerId, userMessageId, turnCt)
                ?? throw new InvalidOperationException("Conversation not found.");

            var newUserMsg = new ConversationMessage(
                newUserMessageId == Guid.Empty ? Guid.NewGuid() : newUserMessageId,
                MessageRole.User, newContent, DateTimeOffset.UtcNow);
            await _conversationStore.AppendMessageAsync(conversationId, callerId, newUserMsg, turnCt);

            // The new user message is appended BEFORE the truncation notice goes out, not after:
            // the notice is what the client acts on (it optimistically re-inserts the edited
            // message), so telling it "truncated to N" before the edit itself is durably stored
            // would let a subsequent AppendMessageAsync failure leave the client showing an edit
            // the server never persisted, with nothing to roll it back. Still before dispatch —
            // see RetryFromMessageAsync's comment and the interface's remarks (#328) for why.
            await SignalHistoryTruncatedAsync(onHistoryTruncated, truncated.Messages.Count, turnCt);

            var outcome = await DispatchTurnAsync(
                sessionKey, conversationId, record.AgentName, newContent, callerId, onChunk, turnCt);

            return outcome with { HistoryKeepCount = truncated.Messages.Count };
        }, ct);
    }

    /// <inheritdoc />
    public async Task ValidateAccessAsync(string conversationId, string callerId, CancellationToken ct)
    {
        var record = await _conversationStore.GetAsync(conversationId, callerId, ct);
        if (record is null)
            throw new InvalidOperationException("Conversation not found.");
    }

    /// <inheritdoc />
    public async Task HandleDisconnectAsync(string sessionKey, Exception? exception, CancellationToken ct)
    {
        var info = _connectionTracker.Untrack(sessionKey);
        if (info is null) return;

        OrchestrationMetrics.ConnectionsActive.Add(-1, new TagList { { AgentConventions.Name, info.AgentName } });

        if (info.TurnCount > 0)
        {
            var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, info.AgentName);
            var elapsed = DateTimeOffset.UtcNow - info.StartedAt;
            OrchestrationMetrics.ConversationDuration.Record(elapsed.TotalMilliseconds, agentTag);
            OrchestrationMetrics.TurnsPerConversation.Record(info.TurnCount, agentTag);
        }

        // This path is where the string "errored" came from; see SessionStatus for what the database
        // did with it and why the parameter is typed now.
        //
        // A deliberate stop is separated out from the other exceptions rather than lumped in with
        // them: a client that navigated away or a host shutting down is the single most common way a
        // conversation ends and is not a failure, and `status` is what the sessions list and the
        // Grafana $status filter show an operator. (An earlier version of this comment justified the
        // change by claiming the dashboards compute an error rate from `status = 'error'`. They do
        // not — the error-rate tiles are Prometheus counters over tool errors, and no panel in
        // Dashboards/ filters on that literal. The change stands on its own; the invented cost did
        // not, and repeating it five times across this file and its tests did not make it true.)
        //
        // On the token check: it is the same rule RunConversationCommandHandler applies, where it
        // genuinely discriminates. Here it does not, and that is worth stating rather than implying
        // otherwise — the only production caller is AgentTelemetryHub.OnDisconnectedAsync passing
        // Context.ConnectionAborted, which SignalR has already cancelled before dispatching (see the
        // note on the write below). So today this reduces to the type test. It is kept because the
        // classification rule is "a stop that was asked for", not "an exception that looks like one",
        // and because a caller that is not the hub would otherwise silently get the wrong answer.
        var status = exception switch
        {
            null => SessionStatus.Completed,
            OperationCanceledException when ct.IsCancellationRequested => SessionStatus.Cancelled,
            _ => SessionStatus.Error,
        };

        // The reason is a stable code, never the exception's own text, and fixing the status above is
        // exactly why that matters now: while the write was being rejected nothing reached the row, so
        // making it land would otherwise have started putting arbitrary exception messages — connection
        // strings, tokens, internal paths — into sessions.error_message, which is read back out and
        // served to clients on the session list. The full exception goes to the log, where it belongs.
        // Same rule, and the same stable-code shape, as RunConversationCommandHandler's error path.
        if (status == SessionStatus.Error)
        {
            _logger.LogError(
                exception,
                "Connection for conversation {ConversationId} dropped with an exception; the session is "
                    + "recorded as errored",
                info.ConversationId);
        }
        else if (status is SessionStatus.Cancelled)
        {
            // A deliberate stop still gets a record, at a level that does not cry wolf. Routing the
            // Error log through a status check alone would have made this branch log nothing at all
            // and drop the exception on the floor — trading an over-reported failure for an
            // unreported one, which is the worse of the two.
            //
            // Branching on `status` in both arms rather than on `exception is not null` here: the two
            // are equivalent, since Cancelled is unreachable with a null exception, but only one of
            // them makes that obvious without re-deriving it.
            _logger.LogDebug(
                exception,
                "Connection for conversation {ConversationId} was cancelled; the session is recorded "
                    + "as cancelled rather than errored",
                info.ConversationId);
        }

        var reason = status switch
        {
            SessionStatus.Error => "connection.dropped_with_exception",
            SessionStatus.Cancelled => "connection.cancelled",
            _ => null,
        };

        try
        {
            // CancellationToken.None, deliberately, and this is load-bearing: `ct` here is
            // Context.ConnectionAborted, and SignalR aborts the connection BEFORE it dispatches
            // disconnect ("Ensure the connection is aborted before firing disconnect", in its own
            // source). So `ct` is already cancelled every single time this method runs. Passing it
            // to the write meant the UPDATE was refused before it was sent — and because the
            // observability store catches every exception on a telemetry write, cancellation
            // included, and logs a warning, nothing surfaced. Every ordinary disconnect left its row
            // status='active' with no ended_at, for ever. Choosing the right status word above is
            // worth nothing if the write that carries it cannot run: cleanup after a cancellation is
            // not itself cancellable. Same rule as RunConversationCommandHandler.EndRunSessionAsync.
            await _observabilityStore.EndSessionAsync(
                info.ObservabilitySessionId,
                status,
                reason,
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to end observability session {SessionId}", info.ObservabilitySessionId);
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="turn"/> holding this conversation's turn lease, and hands it a token that
    /// is cancelled if the lease is lost as well as when the caller cancels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All three turn-producing operations do exactly this, so the acquire/link/release shape lives
    /// here rather than three times — it is the part where a mistake is invisible until two turns
    /// have already interleaved.
    /// </para>
    /// <para>
    /// <strong>What it deliberately does not do is re-read the conversation.</strong> Everything a
    /// turn reads from the record is already read under the lease, inside
    /// <see cref="DispatchTurnAsync"/>, and it has to be read there rather than here: retry and edit
    /// truncate and append <em>after</em> the lease is taken, so a record read at this point would
    /// carry a message count the turn has since changed. The one value taken from the pre-lease read
    /// is <c>AgentName</c>, which no operation on <see cref="IConversationStore"/> can change. If one
    /// ever can, this becomes a stale read and the agent name must move to the late one too.
    /// </para>
    /// <para>
    /// The lost-lease translation is the reason this cannot simply pass the linked token along and
    /// stop there. <see cref="DispatchTurnAsync"/> reads a cancelled token as a client disconnect and
    /// says so in the log; without this, a lease taken by another host would be recorded as the user
    /// closing their browser. The filter checks the caller's token too, so a real disconnect that
    /// happens to race the loss is still reported as a disconnect.
    /// </para>
    /// </remarks>
    private async Task<TurnOutcome> WithTurnLeaseAsync(
        string conversationId,
        Func<CancellationToken, Task<TurnOutcome>> turn,
        CancellationToken ct)
    {
        await using var lease = await _turnLease.AcquireAsync(conversationId, ct);
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.LeaseLost);

        try
        {
            return await turn(turnCts.Token);
        }
        catch (OperationCanceledException)
            when (lease.LeaseLost.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Turn on conversation {ConversationId} stopped: another host took its lease.",
                conversationId);

            throw new InvalidOperationException(ConversationLeaseNotice.Message);
        }
    }

    private async Task<TurnOutcome> DispatchTurnAsync(
        string sessionKey, string conversationId, string agentName, string userMessage,
        string callerId, Func<string, CancellationToken, Task>? onChunk, CancellationToken ct)
    {
        Activity.Current?.SetTag("agent.conversation_id", conversationId);
        Activity.Current?.SetTag(AgentConventions.Name, agentName);
        Activity.Current?.SetTag(UserConventions.UserId, callerId);
        Activity.Current?.AddBaggage("agent.conversation_id", conversationId);
        Activity.Current?.AddBaggage(UserConventions.UserId, callerId);

        var telemetry = await EnsureSessionTrackedAsync(sessionKey, conversationId, agentName, callerId, ct);

        var history = await _conversationStore.GetHistoryForDispatch(
            conversationId, callerId, _config.MaxHistoryMessages, ct) ?? [];

        var updatedRecord = await _conversationStore.GetAsync(conversationId, callerId, ct);

        // Numbered from the conversation's turn count, not its message count. A message count advances
        // by two per turn, so the same conversation produced a different sequence over this transport
        // than over the bundle path — in one key space, on one dashboard (issues #255, #280).
        var turnNumber = telemetry.NextTurnNumber;

        // Conversation-lifetime budget gate: if prior turns already exhausted the cumulative token
        // ceiling, decline this turn gracefully (no LLM dispatch, no cost) with an explanatory
        // assistant message rather than throwing or surfacing an error to the client.
        var budgetStatus = await _conversationBudget.GetStatusAsync(conversationId, ct);
        if (budgetStatus.IsExhausted)
            return await BuildBudgetExhaustedOutcomeAsync(conversationId, callerId, agentName, turnNumber, ct);

        var obsSessionId = telemetry.SessionId;

        var command = new ExecuteAgentTurnCommand
        {
            AgentName = agentName,
            UserMessage = userMessage,
            ConversationHistory = ToMeaiHistory(history),
            ConversationId = conversationId,
            TurnNumber = turnNumber,
            DeploymentOverride = updatedRecord?.Settings?.DeploymentName,
            Temperature = updatedRecord?.Settings?.Temperature,
            SystemPromptOverride = updatedRecord?.Settings?.SystemPromptOverride,
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
        var runTag = new TagList { { AgentConventions.Name, agentName } };
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
            _healthTracker.RecordError(agentName);
            var kind = ex is AiProviderNotConfiguredException ? AgentTurnErrorKind.Configuration : AgentTurnErrorKind.Internal;
            return await HandleTurnErrorAsync(conversationId, callerId, ex, kind, ct);
        }
        finally
        {
            OrchestrationMetrics.RunsActive.Add(-1, runTag);
            AgentTurnStreamSink.Current = previousSink;
        }

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

            _healthTracker.RecordError(agentName);
            return await HandleTurnErrorAsync(conversationId, callerId,
                new InvalidOperationException(result.Error ?? "Agent returned a failure result."),
                result.ErrorKind, ct);
        }

        var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, agentName);
        if (result.ToolsInvoked.Count > 0)
            OrchestrationMetrics.ToolCalls.Add(result.ToolsInvoked.Count, agentTag);

        _healthTracker.RecordSuccess(agentName);

        // Fold this turn's tokens into the conversation-lifetime budget so a subsequent turn is
        // declined once the cumulative ceiling is crossed. No-op when the budget is disabled.
        await _conversationBudget.RecordUsageAsync(
            conversationId, result.InputTokens + result.OutputTokens, ct);

        var userTag = new KeyValuePair<string, object?>(UserConventions.UserId, callerId);
        var userAgentTag = new KeyValuePair<string, object?>(AgentConventions.Name, agentName);
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
        string sessionKey, string conversationId, string agentName, string callerId, CancellationToken ct)
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

        var state = await _telemetryRecorder.BeginAsync(
            conversationId, callerId, agentName, knownRecord: null, ct);

        // Debug, not warning: an empty id is what a host running without an observability database gets
        // on every turn, and that is a supported configuration. At warning level this filled the log of
        // every such deployment with a line about a feature it had chosen not to switch on.
        if (state.SessionId == Guid.Empty)
            _logger.LogDebug("No observability session for conversation {ConversationId}", conversationId);

        if (tracked?.ConversationId == conversationId)
            return state;

        // The gauge counts entries in the tracker, so it moves where entries move — here, next to the
        // Track that replaces one, and not a moment earlier. Decrementing up beside EndSessionAsync
        // reads more naturally and is wrong: BeginAsync above can throw (a cancelled token as the user
        // navigates away, a store that refuses the new conversation), and then Track never runs, the
        // tracker still holds the OLD entry, and the disconnect that eventually arrives decrements it a
        // second time. Two decrements for one increment, on an up-down counter that never recovers —
        // the exact defect this split exists to remove, reintroduced by the split.
        if (leaving is not null)
        {
            OrchestrationMetrics.ConnectionsActive.Add(
                -1, new TagList { { AgentConventions.Name, leaving.AgentName } });
        }

        _connectionTracker.Track(sessionKey, new ActiveConversationInfo(
            conversationId, agentName, callerId, DateTimeOffset.UtcNow,
            state.Totals.TurnCount, state.SessionId,
            state.Totals.InputTokens, state.Totals.OutputTokens,
            state.Totals.CacheRead, state.Totals.CacheWrite,
            state.Totals.CostUsd, state.Totals.ToolCallCount));

        // A connection, not a session and not a conversation: this is the moment one starts watching a
        // conversation, and every decrement is a moment one stops.
        OrchestrationMetrics.ConnectionsActive.Add(1, new TagList { { AgentConventions.Name, agentName } });
        return state;
    }

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

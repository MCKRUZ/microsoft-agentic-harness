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

    /// <inheritdoc />
    public async Task<TurnOutcome> RetryFromMessageAsync(
        string sessionKey, string conversationId, Guid assistantMessageId, string callerId,
        Func<string, CancellationToken, Task>? onChunk, CancellationToken ct)
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

            var outcome = await DispatchTurnAsync(
                sessionKey, conversationId, record.AgentName, last.Content, callerId, onChunk, turnCt);

            return outcome with { HistoryKeepCount = truncated.Messages.Count };
        }, ct);
    }

    /// <inheritdoc />
    public async Task<TurnOutcome> EditAndResubmitAsync(
        string sessionKey, string conversationId, Guid userMessageId, Guid newUserMessageId,
        string newContent, string callerId,
        Func<string, CancellationToken, Task>? onChunk, CancellationToken ct)
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

        // This used to pass the string "errored", which is not one of the three words the sessions
        // table accepts. Postgres rejected the update and the store logged and swallowed it, so a
        // connection that dropped with an exception left its session open forever — no end time,
        // status still active, duration growing. Typed now, so the next wrong word will not compile.
        var status = exception is null ? SessionStatus.Completed : SessionStatus.Error;

        // The reason is a stable code, never the exception's own text, and fixing the status above is
        // exactly why that matters now. While the rejected write was being swallowed, nothing reached
        // the row; making it land would otherwise have put arbitrary exception messages — connection
        // strings, tokens, internal paths — into sessions.error_message, which is read back out and
        // served to clients on the session list. The full exception goes to the log, where it belongs.
        // Same rule, and the same stable-code shape, as RunConversationCommandHandler's error path.
        if (exception is not null)
        {
            _logger.LogError(
                exception,
                "Connection for conversation {ConversationId} dropped with an exception; the session is "
                    + "recorded as errored",
                info.ConversationId);
        }

        try
        {
            await _observabilityStore.EndSessionAsync(
                info.ObservabilitySessionId,
                status,
                exception is null ? null : "connection.dropped_with_exception",
                ct);
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
            assistantMessageId, MessageRole.Assistant, result.Response, DateTimeOffset.UtcNow);
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
        var switchingConversation = tracked is not null && tracked.ConversationId != conversationId;
        if (switchingConversation)
        {
            await _observabilityStore.EndSessionAsync(
                tracked!.ObservabilitySessionId, SessionStatus.Completed, cancellationToken: ct);
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
        if (switchingConversation)
        {
            OrchestrationMetrics.ConnectionsActive.Add(
                -1, new TagList { { AgentConventions.Name, tracked!.AgentName } });
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

    // Delegates to the shared projection rather than repeating the role switch — see
    // ConversationMessageMapping for why three copies of one mapping was a latent bug.
    private static IReadOnlyList<ChatMessage> ToMeaiHistory(IReadOnlyList<ConversationMessage> messages) =>
        ConversationMessageMapping.ToChatMessages(messages);
}

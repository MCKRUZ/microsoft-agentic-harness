using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Application.AI.Common.Models.Conversations;
using Presentation.AgentHub.DTOs;
using Presentation.Common.Extensions;
using Presentation.AgentHub.Interfaces;

namespace Presentation.AgentHub.Hubs;

/// <summary>
/// Thin SignalR adapter that translates WebSocket events to/from
/// <see cref="IConversationOrchestrator"/> calls. All business logic — ownership
/// validation, turn dispatch, metrics, session management — lives in the orchestrator.
/// </summary>
[Authorize]
public sealed class AgentTelemetryHub : Hub
{
    // -------------------------------------------------------------------------
    // Server-to-client event name constants
    // -------------------------------------------------------------------------

    /// <summary>Emitted for each streamed token chunk during an agent turn.</summary>
    public const string EventTokenReceived = "TokenReceived";

    /// <summary>Emitted once the agent turn completes successfully.</summary>
    public const string EventTurnComplete = "TurnComplete";

    /// <summary>Emitted when a tool call begins (sent by the OTel bridge, not this hub).</summary>
    public const string EventToolCallStarted = "ToolCallStarted";

    /// <summary>Emitted when a tool call finishes (sent by the OTel bridge, not this hub).</summary>
    public const string EventToolCallCompleted = "ToolCallCompleted";

    /// <summary>Emitted for each OTel span routed to a conversation or the global-traces group.</summary>
    public const string EventSpanReceived = "SpanReceived";

    /// <summary>Emitted when an agent turn fails. Payload is sanitized — no exception details.</summary>
    public const string EventError = "Error";

    /// <summary>
    /// Emitted after a retry/edit truncates the server-side history. Clients should drop any
    /// local messages beyond <c>keepCount</c> before appending subsequent tokens.
    /// </summary>
    public const string EventHistoryTruncated = "HistoryTruncated";

    /// <summary>
    /// Emitted after the dashboard ingests a new <c>EvalRunReport</c> (Sub-phase 5.4.6).
    /// Payload is a flat object — see <c>EvalRunCompletedPayload</c> in the dashboard
    /// SDK for the contract. Clients use this to refresh the run-history list without
    /// polling.
    /// </summary>
    public const string EventEvalRunCompleted = "EvalRunCompleted";

    /// <summary>
    /// Emitted after each completed agent turn with the Foresight context-window
    /// breakdown for that turn (PR 3). Broadcast to
    /// <see cref="ConversationGroup"/> — only clients already authorised on the
    /// conversation receive it. Payload shape is pinned by
    /// <c>SignalRContextSnapshotNotifierTests</c>.
    /// </summary>
    public const string EventContextSnapshot = "ContextSnapshot";

    /// <summary>SignalR group that receives <see cref="EventEvalRunCompleted"/> broadcasts.</summary>
    public const string EvalDashboardGroup = "eval-dashboard";

    // -------------------------------------------------------------------------
    // Group name helpers
    // -------------------------------------------------------------------------

    internal static string ConversationGroup(string conversationId) => $"conversation:{conversationId}";
    internal const string GlobalTracesGroup = "global-traces";
    private const string GlobalTracesRole = "AgentHub.Traces.ReadAll";

    /// <summary>App role required to subscribe to <see cref="EventEvalRunCompleted"/> broadcasts.</summary>
    internal const string EvalDashboardRole = "AgentHub.EvalDashboard.Read";

    /// <summary>
    /// App role required to subscribe to <see cref="EventContextSnapshot"/> broadcasts
    /// as a read-only observer (Foresight dashboard). Conversation owners get the
    /// events automatically via <see cref="JoinConversationGroup"/>; observers without
    /// ownership use <see cref="SubscribeToConversationSnapshots"/> gated by this role.
    /// </summary>
    internal const string ForesightObserverRole = "AgentHub.Foresight.Observe";

    // -------------------------------------------------------------------------
    // Dependencies
    // -------------------------------------------------------------------------

    private readonly IConversationOrchestrator _orchestrator;
    private readonly ILogger<AgentTelemetryHub> _logger;

    /// <summary>Initialises the hub with the orchestrator and logger.</summary>
    public AgentTelemetryHub(
        IConversationOrchestrator orchestrator,
        ILogger<AgentTelemetryHub> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task OnConnectedAsync() => base.OnConnectedAsync();

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _orchestrator.HandleDisconnectAsync(Context.ConnectionId, exception, Context.ConnectionAborted);
        await base.OnDisconnectedAsync(exception);
    }

    // -------------------------------------------------------------------------
    // Hub methods — conversation lifecycle
    // -------------------------------------------------------------------------

    /// <summary>
    /// Joins or creates a conversation. Returns the last N messages so the client
    /// can restore UI state on reconnect.
    /// </summary>
    public async Task<IReadOnlyList<ConversationMessage>> StartConversation(
        string agentName, string conversationId)
    {
        var ct = Context.ConnectionAborted;
        var callerId = GetCallerId();

        var (record, history) = await _orchestrator.StartConversationAsync(
            Context.ConnectionId, agentName, conversationId, callerId, ct);

        await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(record.Id), ct);

        return history;
    }

    /// <summary>
    /// Replaces the per-conversation agent settings. Throws <see cref="HubException"/>
    /// when the conversation is missing or owned by another user.
    /// </summary>
    public async Task SetConversationSettings(string conversationId, ConversationSettings settings)
    {
        var ct = Context.ConnectionAborted;
        var callerId = GetCallerId();

        try
        {
            await _orchestrator.SetSettingsAsync(conversationId, settings, callerId, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            throw new HubException(ex is UnauthorizedAccessException ? "Access denied." : "Conversation not found.");
        }
    }

    /// <summary>
    /// Sends a user message, dispatches it to the agent pipeline, and streams the response
    /// back as <c>TokenReceived</c> events followed by a <c>TurnComplete</c> event.
    /// </summary>
    public async Task SendMessage(string conversationId, Guid userMessageId, string userMessage)
    {
        var ct = Context.ConnectionAborted;
        var callerId = GetCallerId();

        TurnOutcome outcome;
        try
        {
            outcome = await _orchestrator.SendMessageAsync(
                Context.ConnectionId, conversationId, userMessageId, userMessage, callerId,
                (chunk, cct) => Clients.Caller.SendAsync(EventTokenReceived,
                    new { conversationId, token = chunk, isComplete = false }, cct),
                ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            throw new HubException(ex is UnauthorizedAccessException ? "Access denied." : "Conversation not found.");
        }

        await EmitTurnEventsAsync(conversationId, outcome, ct);
    }

    /// <summary>
    /// Drops the message identified by <paramref name="assistantMessageId"/> and everything
    /// after it, then re-dispatches the preceding user message.
    /// </summary>
    public async Task RetryFromMessage(string conversationId, Guid assistantMessageId)
    {
        var ct = Context.ConnectionAborted;
        var callerId = GetCallerId();
        var historyTruncatedSignaled = false;

        TurnOutcome outcome;
        try
        {
            outcome = await _orchestrator.RetryFromMessageAsync(
                Context.ConnectionId, conversationId, assistantMessageId, callerId,
                (chunk, cct) => Clients.Caller.SendAsync(EventTokenReceived,
                    new { conversationId, token = chunk, isComplete = false }, cct),
                ct,
                EmitHistoryTruncatedAsync(conversationId, () => historyTruncatedSignaled = true));
        }
        catch (Exception ex)
        {
            var mapped = await NotifyPostTruncationFailureAndMapAsync(ex, conversationId, historyTruncatedSignaled, ct);
            if (mapped is not null) throw mapped;
            throw;
        }

        await EmitTurnEventsAsync(conversationId, outcome, ct);
    }

    /// <summary>
    /// Drops the user message identified by <paramref name="userMessageId"/> and everything
    /// after it, appends a new user message, then dispatches to the agent pipeline.
    /// </summary>
    public async Task EditAndResubmit(
        string conversationId, Guid userMessageId, Guid newUserMessageId, string newContent)
    {
        var ct = Context.ConnectionAborted;
        var callerId = GetCallerId();
        var historyTruncatedSignaled = false;

        TurnOutcome outcome;
        try
        {
            outcome = await _orchestrator.EditAndResubmitAsync(
                Context.ConnectionId, conversationId, userMessageId, newUserMessageId, newContent, callerId,
                (chunk, cct) => Clients.Caller.SendAsync(EventTokenReceived,
                    new { conversationId, token = chunk, isComplete = false }, cct),
                ct,
                EmitHistoryTruncatedAsync(conversationId, () => historyTruncatedSignaled = true));
        }
        catch (Exception ex)
        {
            var mapped = await NotifyPostTruncationFailureAndMapAsync(ex, conversationId, historyTruncatedSignaled, ct);
            if (mapped is not null) throw mapped;
            throw;
        }

        await EmitTurnEventsAsync(conversationId, outcome, ct);
    }

    /// <summary>
    /// Invokes a named tool through the agent pipeline using a structured tool
    /// invocation marker that the agent framework parses as a direct tool call,
    /// not as natural language input (prevents prompt injection via tool parameters).
    /// </summary>
    public async Task InvokeToolViaAgent(string conversationId, string toolName, string inputJson)
    {
        if (string.IsNullOrWhiteSpace(toolName) || toolName.Length > 128)
            throw new HubException("Invalid tool name.");

        if (inputJson is not null && inputJson.Length > 32_768)
            throw new HubException("Input too large.");

        var structuredMessage = $"[TOOL_INVOKE:{Uri.EscapeDataString(toolName)}]{inputJson}";
        await SendMessage(conversationId, Guid.NewGuid(), structuredMessage);
    }

    // -------------------------------------------------------------------------
    // Hub methods — group management
    // -------------------------------------------------------------------------

    /// <summary>Adds this connection to the conversation's SignalR group.</summary>
    public async Task JoinConversationGroup(string conversationId)
    {
        var ct = Context.ConnectionAborted;
        var callerId = GetCallerId();

        try
        {
            await _orchestrator.ValidateAccessAsync(conversationId, callerId, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            throw new HubException(ex is UnauthorizedAccessException ? "Access denied." : "Conversation not found.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(conversationId), ct);
    }

    /// <summary>Removes this connection from the conversation's SignalR group.</summary>
    public async Task LeaveConversationGroup(string conversationId)
    {
        var ct = Context.ConnectionAborted;
        var callerId = GetCallerId();

        try
        {
            await _orchestrator.ValidateAccessAsync(conversationId, callerId, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            throw new HubException(ex is UnauthorizedAccessException ? "Access denied." : "Conversation not found.");
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationGroup(conversationId), ct);
    }

    // -------------------------------------------------------------------------
    // Hub methods — global trace firehose
    // -------------------------------------------------------------------------

    /// <summary>
    /// Subscribes this connection to the global OpenTelemetry span firehose.
    /// Requires the <c>AgentHub.Traces.ReadAll</c> app role.
    /// </summary>
    public async Task JoinGlobalTraces()
    {
        if (!Context.User!.IsInRole(GlobalTracesRole))
            throw new HubException($"The {GlobalTracesRole} role is required to subscribe to global traces.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GlobalTracesGroup, Context.ConnectionAborted);
        _logger.LogInformation("Connection {ConnectionId} joined global-traces.", Context.ConnectionId);
    }

    /// <summary>Unsubscribes this connection from the global trace firehose.</summary>
    public Task LeaveGlobalTraces() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GlobalTracesGroup, Context.ConnectionAborted);

    // -------------------------------------------------------------------------
    // Hub methods — eval dashboard subscription
    // -------------------------------------------------------------------------

    /// <summary>
    /// Subscribes this connection to <see cref="EventEvalRunCompleted"/> broadcasts.
    /// Requires the <see cref="EvalDashboardRole"/> app role — broadcasts carry run
    /// metadata (RunId, TotalCostUsd, pass/fail counts) for every ingested run, so
    /// access is gated explicitly rather than implied by hub auth. Mirrors the
    /// <see cref="JoinGlobalTraces"/> role-gate pattern.
    /// </summary>
    public async Task JoinEvalDashboard()
    {
        if (!Context.User!.IsInRole(EvalDashboardRole))
            throw new HubException($"The {EvalDashboardRole} role is required to subscribe to eval-dashboard broadcasts.");

        await Groups.AddToGroupAsync(Context.ConnectionId, EvalDashboardGroup, Context.ConnectionAborted);
        _logger.LogInformation("Connection {ConnectionId} joined eval-dashboard.", Context.ConnectionId);
    }

    /// <summary>Unsubscribes this connection from eval-dashboard broadcasts.</summary>
    public Task LeaveEvalDashboard() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, EvalDashboardGroup, Context.ConnectionAborted);

    // -------------------------------------------------------------------------
    // Hub methods — Foresight read-only conversation observer
    // -------------------------------------------------------------------------

    /// <summary>
    /// Subscribes this connection to <see cref="EventContextSnapshot"/> broadcasts
    /// for <paramref name="conversationId"/> as a read-only observer. Used by the
    /// Foresight dashboard SPA where the connected user is NOT the conversation
    /// owner — the owner-gated <see cref="JoinConversationGroup"/> would reject.
    /// Requires the <see cref="ForesightObserverRole"/> app role.
    /// </summary>
    /// <remarks>
    /// Broadcasts carry per-turn token breakdowns and loaded artifact names that
    /// could leak prompt structure to unauthorized observers; the role gate is
    /// the access boundary. Mirrors the <see cref="JoinEvalDashboard"/> pattern.
    /// </remarks>
    public async Task SubscribeToConversationSnapshots(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || conversationId.Length > 256)
            throw new HubException("Invalid conversationId.");

        if (!Context.User!.IsInRole(ForesightObserverRole))
            throw new HubException($"The {ForesightObserverRole} role is required to observe conversation snapshots.");

        await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(conversationId), Context.ConnectionAborted);
        _logger.LogInformation("Connection {ConnectionId} subscribed to snapshots for conversation {ConversationId}.",
            Context.ConnectionId, conversationId);
    }

    /// <summary>Unsubscribes this observer connection from snapshots for the given conversation.</summary>
    public Task UnsubscribeFromConversationSnapshots(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || conversationId.Length > 256)
            throw new HubException("Invalid conversationId.");

        return Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationGroup(conversationId), Context.ConnectionAborted);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private string GetCallerId() => Context.User?.GetUserId()
        ?? throw new HubException("Unable to determine caller identity.");

    /// <summary>
    /// Builds the callback <see cref="IConversationOrchestrator.RetryFromMessageAsync"/> and
    /// <see cref="IConversationOrchestrator.EditAndResubmitAsync"/> invoke immediately after
    /// truncating history, before dispatching the turn — see those methods' remarks on why
    /// <c>HistoryTruncated</c> must reach the client before this turn's own streamed deltas do (#328).
    /// </summary>
    /// <param name="onInvoked">
    /// Called synchronously, before the SignalR send, purely to record that truncation was
    /// signalled — regardless of whether the send itself succeeds. The orchestrator swallows a
    /// failed send (see <c>ConversationOrchestrator.SignalHistoryTruncatedAsync</c>) rather than
    /// aborting the turn, but the caller still needs to know a client may already have acted on the
    /// notice, so a later failure in this same turn can be surfaced rather than left as a silent gap.
    /// </param>
    private Func<int, CancellationToken, Task> EmitHistoryTruncatedAsync(string conversationId, Action onInvoked) =>
        (keepCount, cct) =>
        {
            onInvoked();
            return Clients.Caller.SendAsync(EventHistoryTruncated, new { conversationId, keepCount }, cct);
        };

    /// <summary>
    /// Emits an <c>Error</c> event for a turn that failed AFTER the client was already told its
    /// history was truncated. A bare <see cref="HubException"/> only surfaces to the caller's RPC
    /// promise, never through the client's <c>Error</c> event handler — leaving it showing a
    /// truncated transcript with no explanation and no retried response. Called only from
    /// <see cref="NotifyPostTruncationFailureAndMapAsync"/>.
    /// </summary>
    private Task EmitPostTruncationFailureAsync(string conversationId, CancellationToken ct) =>
        Clients.Caller.SendAsync(EventError,
            new
            {
                conversationId,
                message = "The turn could not be completed after truncating history. Reload the conversation to resynchronize.",
                code = "AGENT_ERROR",
            }, ct);

    /// <summary>
    /// Shared failure handling for <see cref="RetryFromMessage"/> and <see cref="EditAndResubmit"/>:
    /// best-effort-notifies the client of a post-truncation failure when it may already have acted
    /// on the truncation notice, then returns the <see cref="HubException"/> the caller should throw
    /// for a recognised failure type, or <see langword="null"/> when the caller should instead
    /// rethrow <paramref name="ex"/> itself via a bare <c>throw;</c> (which must stay in the
    /// caller's own catch block to preserve the original stack trace — this helper cannot do that
    /// rethrow on the caller's behalf).
    /// </summary>
    /// <remarks>
    /// The notify runs for EVERY exception type, not just the two mapped to a
    /// <see cref="HubException"/> below — the client may already have acted on the truncation
    /// notice regardless of what failed afterward. This must happen before, not conditioned on,
    /// the typed-exception mapping: if a caller instead used a bare
    /// <c>catch (Exception) when (historyTruncatedSignaled)</c> clause placed below a typed catch
    /// clause, C#'s top-down clause matching would let the typed clause win first for
    /// <see cref="InvalidOperationException"/>/<see cref="UnauthorizedAccessException"/> — which are
    /// exactly the types this path actually throws (a stolen turn lease,
    /// <c>ConversationAccessDeniedException</c>) — silently defeating the guard for the failures it
    /// exists for. This shape was shipped once and caught by CI's correctness-review gate.
    /// </remarks>
    private async Task<HubException?> NotifyPostTruncationFailureAndMapAsync(
        Exception ex, string conversationId, bool historyTruncatedSignaled, CancellationToken ct)
    {
        if (historyTruncatedSignaled)
        {
            try
            {
                // Best-effort: ct may already be cancelled if ex itself is the client
                // disconnecting, in which case this send can throw too — that must not replace or
                // hide ex, which the caller still needs to map or rethrow after this returns.
                await EmitPostTruncationFailureAsync(conversationId, ct);
            }
            catch (Exception notifyEx)
            {
                _logger.LogWarning(notifyEx,
                    "Failed to notify the client of a post-truncation failure for conversation {ConversationId}.",
                    conversationId);
            }
        }

        return ex switch
        {
            UnauthorizedAccessException => new HubException("Access denied."),
            // The "retry" substring check only ever matches RetryFromMessage's own
            // "Cannot retry: no preceding user message found." — EditAndResubmit has no failure
            // path producing that text, so sharing this check between both callers is safe.
            InvalidOperationException when ex.Message.Contains("retry") => new HubException(ex.Message),
            InvalidOperationException => new HubException("Conversation not found."),
            _ => null,
        };
    }

    /// <summary>
    /// Emits the standard post-turn events to the caller: either (final TokenReceived +
    /// TurnComplete) or Error. <c>HistoryTruncated</c>, when this turn truncated history, was
    /// already emitted before dispatch via <see cref="EmitHistoryTruncatedAsync"/> — never here.
    /// </summary>
    private async Task EmitTurnEventsAsync(string conversationId, TurnOutcome outcome, CancellationToken ct)
    {
        if (outcome.Success)
        {
            await Clients.Caller.SendAsync(EventTokenReceived,
                new { conversationId, token = outcome.Response, isComplete = true }, ct);

            await Clients.Caller.SendAsync(EventTurnComplete,
                new
                {
                    conversationId,
                    turnNumber = outcome.FinalTurnNumber,
                    fullResponse = outcome.Response,
                    assistantMessageId = outcome.AssistantMessageId,
                    budgetExhausted = outcome.BudgetExhausted,
                }, ct);
        }
        else
        {
            await Clients.Caller.SendAsync(EventError,
                new { conversationId, message = outcome.ErrorMessage ?? "An error occurred.", code = "AGENT_ERROR" }, ct);
        }
    }
}

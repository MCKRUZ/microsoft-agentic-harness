using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Extensions;
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

    // -------------------------------------------------------------------------
    // Group name helpers
    // -------------------------------------------------------------------------

    internal static string ConversationGroup(string conversationId) => $"conversation:{conversationId}";
    internal const string GlobalTracesGroup = "global-traces";
    private const string GlobalTracesRole = "AgentHub.Traces.ReadAll";

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

        TurnOutcome outcome;
        try
        {
            outcome = await _orchestrator.RetryFromMessageAsync(
                Context.ConnectionId, conversationId, assistantMessageId, callerId,
                (chunk, cct) => Clients.Caller.SendAsync(EventTokenReceived,
                    new { conversationId, token = chunk, isComplete = false }, cct),
                ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            throw new HubException(ex is UnauthorizedAccessException
                ? "Access denied."
                : ex.Message.Contains("retry") ? ex.Message : "Conversation not found.");
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

        TurnOutcome outcome;
        try
        {
            outcome = await _orchestrator.EditAndResubmitAsync(
                Context.ConnectionId, conversationId, userMessageId, newUserMessageId, newContent, callerId,
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
    // Private helpers
    // -------------------------------------------------------------------------

    private string GetCallerId() => Context.User?.GetUserId()
        ?? throw new HubException("Unable to determine caller identity.");

    /// <summary>
    /// Emits the standard post-turn events to the caller: optional HistoryTruncated,
    /// then either (final TokenReceived + TurnComplete) or Error.
    /// </summary>
    private async Task EmitTurnEventsAsync(string conversationId, TurnOutcome outcome, CancellationToken ct)
    {
        if (outcome.HistoryKeepCount.HasValue)
        {
            await Clients.Caller.SendAsync(EventHistoryTruncated,
                new { conversationId, keepCount = outcome.HistoryKeepCount.Value }, ct);
        }

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
                }, ct);
        }
        else
        {
            await Clients.Caller.SendAsync(EventError,
                new { conversationId, message = outcome.ErrorMessage ?? "An error occurred.", code = "AGENT_ERROR" }, ct);
        }
    }
}

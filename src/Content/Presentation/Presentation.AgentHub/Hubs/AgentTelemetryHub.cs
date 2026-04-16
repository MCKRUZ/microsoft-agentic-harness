using System.Diagnostics;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Presentation.AgentHub.Extensions;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Models;

namespace Presentation.AgentHub.Hubs;

/// <summary>
/// SignalR hub that connects browser clients to the agent execution pipeline.
/// Handles conversation lifecycle, streaming agent responses, and telemetry subscriptions.
///
/// Authentication: all hub methods require a valid Azure AD bearer token, passed via
/// the <c>access_token</c> query parameter for WebSocket upgrades (wired in DependencyInjection.cs).
///
/// Ownership: every conversation-scoped method calls <see cref="ValidateOwnershipAsync"/>
/// and throws <see cref="HubException"/> on cross-user access — no ownership details
/// are surfaced to the caller.
///
/// Turn serialization: <see cref="ConversationLockRegistry"/> (singleton) provides one
/// <see cref="SemaphoreSlim"/> per conversation, preventing concurrent <c>SendMessage</c>
/// calls from interleaving token streams or corrupting the conversation record.
/// </summary>
[Authorize]
public sealed class AgentTelemetryHub : Hub
{
    // -------------------------------------------------------------------------
    // Server-to-client event name constants
    // Shared with the OTel bridge (section 05) and tests (section 07) to avoid magic strings.
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

    // -------------------------------------------------------------------------
    // Group name helpers
    // -------------------------------------------------------------------------

    internal static string ConversationGroup(string conversationId) => $"conversation:{conversationId}";
    internal const string GlobalTracesGroup = "global-traces";
    private const string GlobalTracesRole = "AgentHub.Traces.ReadAll";

    // -------------------------------------------------------------------------
    // Dependencies
    // -------------------------------------------------------------------------

    private readonly IMediator _mediator;
    private readonly IConversationStore _conversationStore;
    private readonly ConversationLockRegistry _lockRegistry;
    private readonly ILogger<AgentTelemetryHub> _logger;
    private readonly AgentHubConfig _config;

    /// <summary>Initialises the hub with all required dependencies.</summary>
    public AgentTelemetryHub(
        IMediator mediator,
        IConversationStore conversationStore,
        ConversationLockRegistry lockRegistry,
        IOptions<AgentHubConfig> config,
        ILogger<AgentTelemetryHub> logger)
    {
        _mediator = mediator;
        _conversationStore = conversationStore;
        _lockRegistry = lockRegistry;
        _config = config.Value;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Hub methods — conversation lifecycle
    // -------------------------------------------------------------------------

    /// <summary>
    /// Joins or creates a conversation. Returns the last <see cref="AgentHubConfig.MaxHistoryMessages"/>
    /// messages so the client can restore UI state on reconnect.
    ///
    /// If <paramref name="conversationId"/> is empty or no matching record exists a new conversation
    /// is created; the caller receives a <c>ConversationStarted</c> event with the generated ID.
    /// If a matching record exists, ownership is validated before joining.
    /// </summary>
    /// <returns>The conversation's message history (empty for new conversations).</returns>
    public async Task<IReadOnlyList<ConversationMessage>> StartConversation(
        string agentName,
        string conversationId)
    {
        var ct = Context.ConnectionAborted;
        var callerId = GetCallerId();
        var existingRecord = await ValidateOwnershipAsync(conversationId, callerId, ct);

        ConversationRecord record;
        if (existingRecord is null)
        {
            // Create a new conversation, using the client-supplied GUID if provided (idempotent reconnect).
            record = await _conversationStore.CreateAsync(
                agentName, callerId,
                conversationId: string.IsNullOrWhiteSpace(conversationId) ? null : conversationId,
                ct: ct);

            _logger.LogInformation("Created conversation {ConversationId} for user {UserId}.",
                record.Id, callerId);
        }
        else
        {
            record = existingRecord;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(record.Id), ct);

        var history = await _conversationStore.GetHistoryForDispatch(
            record.Id, _config.MaxHistoryMessages, ct) ?? [];

        return history;
    }

    /// <summary>
    /// Sends a user message, dispatches it to the agent pipeline, and streams the response
    /// back as <c>TokenReceived</c> events followed by a <c>TurnComplete</c> event.
    ///
    /// Turn serialization: per-conversation <see cref="SemaphoreSlim"/> from
    /// <see cref="ConversationLockRegistry"/> ensures that concurrent <c>SendMessage</c>
    /// calls on the same conversation complete in order — no interleaved token streams.
    /// </summary>
    public async Task SendMessage(string conversationId, string userMessage)
    {
        var ct = Context.ConnectionAborted;
        var callerId = GetCallerId();
        var record = await ValidateOwnershipAsync(conversationId, callerId, ct)
            ?? throw new HubException("Conversation not found.");

        var semaphore = _lockRegistry.GetOrCreate(conversationId);
        await semaphore.WaitAsync(ct);
        try
        {
            // Append user message to the store.
            var userMsg = new ConversationMessage(MessageRole.User, userMessage, DateTimeOffset.UtcNow);
            await _conversationStore.AppendMessageAsync(conversationId, userMsg, ct);

            // Tag current activity so the OTel bridge (section 05) can route spans
            // to the correct conversation group.
            Activity.Current?.SetTag("agent.conversation_id", conversationId);

            // Retrieve truncated history for dispatch.
            var history = await _conversationStore.GetHistoryForDispatch(
                conversationId, _config.MaxHistoryMessages, ct) ?? [];

            var updatedRecord = await _conversationStore.GetAsync(conversationId, ct);
            var turnNumber = updatedRecord?.Messages.Count ?? 0;

            var command = new ExecuteAgentTurnCommand
            {
                // Use the agent name stored when the conversation was created so that
                // each conversation is bound to the agent the user originally selected.
                AgentName = record.AgentName,
                UserMessage = userMessage,
                ConversationHistory = ToMeaiHistory(history),
                ConversationId = conversationId,
                TurnNumber = turnNumber,
            };

            AgentTurnResult result;
            try
            {
                result = await _mediator.Send(command, ct);
            }
            catch (Exception ex)
            {
                await HandleTurnErrorAsync(conversationId, ex, ct);
                return;
            }

            if (!result.Success)
            {
                await HandleTurnErrorAsync(conversationId,
                    new InvalidOperationException(result.Error ?? "Agent returned a failure result."), ct);
                return;
            }

            // TODO: Replace simulated 50-char chunking with real IAsyncEnumerable<string> streaming
            // when ExecuteAgentTurnCommand supports it. Current implementation chunks the completed
            // response after the fact, which provides the streaming UX without true streaming semantics.
            await StreamResponseAsync(conversationId, result.Response, ct);

            // Append assistant response to the store.
            var assistantMsg = new ConversationMessage(MessageRole.Assistant, result.Response, DateTimeOffset.UtcNow);
            await _conversationStore.AppendMessageAsync(conversationId, assistantMsg, ct);

            var finalRecord = await _conversationStore.GetAsync(conversationId, ct);
            var finalTurnNumber = finalRecord?.Messages.Count ?? turnNumber + 1;

            await Clients.Caller.SendAsync(EventTurnComplete,
                new { conversationId, turnNumber = finalTurnNumber, fullResponse = result.Response }, ct);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Invokes a named tool through the agent pipeline by synthesising a user message.
    /// Ownership is validated via the underlying <see cref="SendMessage"/> call.
    /// </summary>
    public async Task InvokeToolViaAgent(string conversationId, string toolName, string inputJson)
    {
        // Ownership validation is delegated to SendMessage — no double-check needed here.
        var userMessage = $"Please invoke the tool '{toolName}' with the following input: {inputJson}";
        await SendMessage(conversationId, userMessage);
    }

    /// <summary>Adds this connection to the conversation's SignalR group.</summary>
    public async Task JoinConversationGroup(string conversationId)
    {
        var ct = Context.ConnectionAborted;
        var callerId = GetCallerId();
        _ = await ValidateOwnershipAsync(conversationId, callerId, ct)
            ?? throw new HubException("Conversation not found.");

        await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(conversationId), ct);
    }

    /// <summary>Removes this connection from the conversation's SignalR group. No ownership check — leaving is always safe.</summary>
    public Task LeaveConversationGroup(string conversationId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationGroup(conversationId), Context.ConnectionAborted);

    // -------------------------------------------------------------------------
    // Hub methods — global trace firehose
    // -------------------------------------------------------------------------

    /// <summary>
    /// Subscribes this connection to the global OpenTelemetry span firehose.
    ///
    /// Requires the <c>AgentHub.Traces.ReadAll</c> app role, which must be assigned in
    /// the Azure AD app registration under <b>App roles</b>. It is intentionally absent
    /// from the default tenant assignment — grant it only to internal observability users.
    /// </summary>
    /// <exception cref="HubException">Thrown if the caller lacks the required role.</exception>
    public async Task JoinGlobalTraces()
    {
        if (!Context.User!.IsInRole(GlobalTracesRole))
            throw new HubException($"The {GlobalTracesRole} role is required to subscribe to global traces.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GlobalTracesGroup, Context.ConnectionAborted);
        _logger.LogInformation("Connection {ConnectionId} joined global-traces.", Context.ConnectionId);
    }

    /// <summary>Unsubscribes this connection from the global trace firehose. No role check.</summary>
    public Task LeaveGlobalTraces() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GlobalTracesGroup, Context.ConnectionAborted);

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private string GetCallerId() => Context.User?.GetUserId()
        ?? throw new HubException("Unable to determine caller identity.");

    /// <summary>
    /// Returns the conversation record if <paramref name="conversationId"/> is non-empty and exists,
    /// or <c>null</c> if it doesn't exist. Throws <see cref="HubException"/> if the record
    /// exists but belongs to a different user.
    /// </summary>
    private async Task<ConversationRecord?> ValidateOwnershipAsync(
        string conversationId,
        string callerId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return null;

        var record = await _conversationStore.GetAsync(conversationId, ct);
        if (record is null)
            return null;

        if (record.UserId != callerId)
        {
            _logger.LogWarning(
                "User {CallerId} attempted to access conversation {ConversationId} owned by {OwnerId}.",
                callerId, conversationId, record.UserId);
            throw new HubException("Access denied.");
        }

        return record;
    }

    /// <summary>
    /// Chunks <paramref name="response"/> into 50-character segments and sends each as a
    /// <c>TokenReceived</c> event to the caller. Sends a final chunk with <c>isComplete = true</c>.
    /// </summary>
    private async Task StreamResponseAsync(string conversationId, string response, CancellationToken ct)
    {
        const int chunkSize = 50;
        for (var i = 0; i < response.Length; i += chunkSize)
        {
            var chunk = response.Substring(i, Math.Min(chunkSize, response.Length - i));
            await Clients.Caller.SendAsync(EventTokenReceived,
                new { conversationId, token = chunk, isComplete = false }, ct);
        }

        // Final marker with the full response for clients that prefer a single completion signal.
        await Clients.Caller.SendAsync(EventTokenReceived,
            new { conversationId, token = response, isComplete = true }, ct);
    }

    /// <summary>
    /// Handles an agent turn error: logs the full exception server-side, appends a synthetic
    /// error message to the conversation store, and sends a sanitized <c>Error</c> event to
    /// the caller. Never surfaces exception details to the client.
    /// </summary>
    private async Task HandleTurnErrorAsync(string conversationId, Exception ex, CancellationToken ct)
    {
        _logger.LogError(ex, "Agent turn failed for conversation {ConversationId}.", conversationId);

        try
        {
            var errorMsg = new ConversationMessage(
                MessageRole.Assistant,
                "[Error] The agent encountered an error.",
                DateTimeOffset.UtcNow);
            await _conversationStore.AppendMessageAsync(conversationId, errorMsg, ct);
        }
        catch (Exception storeEx)
        {
            _logger.LogError(storeEx, "Failed to append error message to conversation {ConversationId}.", conversationId);
        }

        await Clients.Caller.SendAsync(EventError,
            new { conversationId, message = "An error occurred processing your request.", code = "AGENT_ERROR" }, ct);
    }

    /// <summary>
    /// Converts the store's <see cref="ConversationMessage"/> domain model to
    /// <see cref="ChatMessage"/> instances expected by <see cref="ExecuteAgentTurnCommand"/>.
    /// </summary>
    private static IReadOnlyList<ChatMessage> ToMeaiHistory(IReadOnlyList<ConversationMessage> messages) =>
        messages.Select(m => new ChatMessage(ToChatRole(m.Role), m.Content)).ToList();

    private static ChatRole ToChatRole(MessageRole role) => role switch
    {
        MessageRole.User => ChatRole.User,
        MessageRole.Assistant => ChatRole.Assistant,
        MessageRole.System => ChatRole.System,
        MessageRole.Tool => ChatRole.Tool,
        _ => ChatRole.User,
    };
}

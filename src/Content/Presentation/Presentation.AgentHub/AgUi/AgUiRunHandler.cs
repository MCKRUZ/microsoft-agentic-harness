using System.Security.Claims;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Hubs;
using Presentation.AgentHub.Interfaces;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Orchestrates a single AG-UI run: validates ownership, acquires the conversation lock,
/// dispatches to the agent pipeline via MediatR, and emits AG-UI SSE events.
/// </summary>
/// <remarks>
/// This mirrors the logic in <c>AgentTelemetryHub.DispatchTurnAsync</c> but targets
/// the AG-UI SSE protocol instead of SignalR. Register as a scoped service.
/// </remarks>
public sealed class AgUiRunHandler
{
    private const int ChunkSize = 50;

    private readonly IMediator _mediator;
    private readonly IConversationStore _conversationStore;
    private readonly ConversationLockRegistry _lockRegistry;
    private readonly ILogger<AgUiRunHandler> _logger;

    /// <summary>
    /// Initializes a new <see cref="AgUiRunHandler"/>.
    /// </summary>
    public AgUiRunHandler(
        IMediator mediator,
        IConversationStore conversationStore,
        ConversationLockRegistry lockRegistry,
        ILogger<AgUiRunHandler> logger)
    {
        _mediator = mediator;
        _conversationStore = conversationStore;
        _lockRegistry = lockRegistry;
        _logger = logger;
    }

    /// <summary>
    /// Handles an AG-UI run request end-to-end.
    /// </summary>
    /// <param name="input">The deserialized <c>RunAgentInput</c> from the request body.</param>
    /// <param name="writer">The SSE event writer targeting the HTTP response stream.</param>
    /// <param name="user">The authenticated user principal from the HTTP context.</param>
    /// <param name="ct">Cancellation token (triggered on client disconnect).</param>
    public async Task HandleRunAsync(
        RunAgentInput input,
        IAgUiEventWriter writer,
        ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        await writer.WriteAsync(new RunStartedEvent(input.ThreadId, input.RunId), ct);

        string callerId;
        try
        {
            callerId = GetCallerId(user);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "AG-UI run rejected — missing identity claim.");
            await writer.WriteAsync(new RunErrorEvent("Unable to determine caller identity."), ct);
            return;
        }

        ConversationRecord? record;
        try
        {
            record = await _conversationStore.GetAsync(input.ThreadId, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AG-UI run {RunId}: error loading conversation {ThreadId}.", input.RunId, input.ThreadId);
            await writer.WriteAsync(new RunErrorEvent("An error occurred loading the conversation."), ct);
            return;
        }

        if (record is null)
        {
            _logger.LogWarning("AG-UI run {RunId}: conversation {ThreadId} not found.", input.RunId, input.ThreadId);
            await writer.WriteAsync(new RunErrorEvent("Conversation not found."), ct);
            return;
        }

        if (record.UserId != callerId)
        {
            _logger.LogWarning(
                "AG-UI run {RunId}: user {CallerId} attempted to access conversation {ThreadId} owned by {OwnerId}.",
                input.RunId, callerId, input.ThreadId, record.UserId);
            await writer.WriteAsync(new RunErrorEvent("Access denied."), ct);
            return;
        }

        var userMessage = input.Messages
            .LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));

        if (userMessage is null || string.IsNullOrWhiteSpace(userMessage.Content))
        {
            _logger.LogWarning("AG-UI run {RunId}: no user message found in input.", input.RunId);
            await writer.WriteAsync(new RunErrorEvent("No user message found in the request."), ct);
            return;
        }

        var semaphore = _lockRegistry.GetOrCreate(input.ThreadId);
        await semaphore.WaitAsync(ct);
        try
        {
            await ExecuteRunAsync(input, writer, record, userMessage.Content, ct);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — no event to emit.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AG-UI run {RunId}: unhandled error during turn execution.", input.RunId);
            await TryWriteErrorAsync(writer, "An unexpected error occurred.", ct);
        }
        finally
        {
            semaphore.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task ExecuteRunAsync(
        RunAgentInput input,
        IAgUiEventWriter writer,
        ConversationRecord record,
        string userMessageText,
        CancellationToken ct)
    {
        // Append user message to the persisted conversation.
        var userMsg = new ConversationMessage(
            Guid.NewGuid(),
            MessageRole.User,
            userMessageText,
            DateTimeOffset.UtcNow);
        await _conversationStore.AppendMessageAsync(input.ThreadId, userMsg, ct);

        // Load truncated history for dispatch (mirrors hub's MaxHistoryMessages).
        // Use a reasonable default — the hub reads this from config; we use 50 here
        // since AgUiRunHandler is not wired to AgentHubConfig directly.
        var history = await _conversationStore.GetHistoryForDispatch(input.ThreadId, 50, ct) ?? [];
        var turnNumber = record.Messages.Count + 1;

        var command = new ExecuteAgentTurnCommand
        {
            AgentName = record.AgentName,
            UserMessage = userMessageText,
            ConversationHistory = ToMeaiHistory(history),
            ConversationId = input.ThreadId,
            TurnNumber = turnNumber,
            DeploymentOverride = record.Settings?.DeploymentName,
            Temperature = record.Settings?.Temperature,
            SystemPromptOverride = record.Settings?.SystemPromptOverride,
        };

        AgentTurnResult result;
        try
        {
            result = await _mediator.Send(command, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AG-UI run {RunId}: MediatR dispatch failed.", input.RunId);
            await writer.WriteAsync(new RunErrorEvent("An error occurred during agent execution."), ct);
            return;
        }

        if (!result.Success)
        {
            _logger.LogWarning("AG-UI run {RunId}: agent returned failure — {Error}.", input.RunId, result.Error);
            await writer.WriteAsync(new RunErrorEvent("The agent was unable to process your request."), ct);
            return;
        }

        // Stream the response as TEXT_MESSAGE_* events.
        var messageId = Guid.NewGuid().ToString();
        await writer.WriteAsync(new TextMessageStartEvent(messageId, "assistant"), ct);

        var response = result.Response;
        for (var i = 0; i < response.Length; i += ChunkSize)
        {
            var chunk = response.Substring(i, Math.Min(ChunkSize, response.Length - i));
            await writer.WriteAsync(new TextMessageContentEvent(messageId, chunk), ct);
        }

        await writer.WriteAsync(new TextMessageEndEvent(messageId), ct);

        // Persist the assistant response.
        var assistantMsg = new ConversationMessage(
            Guid.NewGuid(),
            MessageRole.Assistant,
            response,
            DateTimeOffset.UtcNow);
        await _conversationStore.AppendMessageAsync(input.ThreadId, assistantMsg, ct);

        await writer.WriteAsync(new RunFinishedEvent(input.ThreadId, input.RunId), ct);
    }

    /// <summary>
    /// Extracts the Azure AD object ID (OID) from the user principal.
    /// Mirrors <c>ClaimsPrincipalExtensions.GetUserId()</c>.
    /// </summary>
    private static string GetCallerId(ClaimsPrincipal principal)
    {
        var oid = principal.FindFirstValue("oid")
            ?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");

        if (string.IsNullOrEmpty(oid))
            throw new InvalidOperationException("The 'oid' claim is missing from the authenticated user's token.");

        return oid;
    }

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

    private static async Task TryWriteErrorAsync(IAgUiEventWriter writer, string message, CancellationToken ct)
    {
        try
        {
            await writer.WriteAsync(new RunErrorEvent(message), ct);
        }
        catch
        {
            // Stream may already be closed — swallow silently.
        }
    }
}

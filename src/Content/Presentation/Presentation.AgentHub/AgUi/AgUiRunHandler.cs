using System.Diagnostics;
using System.Security.Claims;
using Application.AI.Common.Interfaces;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Telemetry.Conventions;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
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
    private readonly IObservabilityStore _observabilityStore;
    private readonly ConversationLockRegistry _lockRegistry;
    private readonly IAgUiEventWriterAccessor _writerAccessor;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<AgUiRunHandler> _logger;

    /// <summary>
    /// Initializes a new <see cref="AgUiRunHandler"/>.
    /// </summary>
    public AgUiRunHandler(
        IMediator mediator,
        IConversationStore conversationStore,
        IObservabilityStore observabilityStore,
        ConversationLockRegistry lockRegistry,
        IAgUiEventWriterAccessor writerAccessor,
        IHostEnvironment environment,
        ILogger<AgUiRunHandler> logger)
    {
        _mediator = mediator;
        _conversationStore = conversationStore;
        _observabilityStore = observabilityStore;
        _lockRegistry = lockRegistry;
        _writerAccessor = writerAccessor;
        _environment = environment;
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

        var observabilitySessionId = record.ObservabilitySessionId ?? Guid.Empty;
        if (observabilitySessionId == Guid.Empty)
        {
            var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, record.AgentName);
            SessionMetrics.SessionsStarted.Add(1, agentTag);
            SessionMetrics.ActiveSessions.Add(1, new TagList { { AgentConventions.Name, record.AgentName } });
            UserActivityMetrics.SessionsStarted.Add(1,
                new KeyValuePair<string, object?>(UserConventions.UserId, callerId));

            observabilitySessionId = await _observabilityStore.StartSessionAsync(
                input.ThreadId, record.AgentName, model: null, ct);

            await _conversationStore.UpdateTelemetryAsync(
                input.ThreadId, observabilitySessionId, TelemetryAccumulator.Zero, ct);
        }

        Activity.Current?.AddBaggage(UserConventions.UserId, callerId);

        var semaphore = _lockRegistry.GetOrCreate(input.ThreadId);
        await semaphore.WaitAsync(ct);
        try
        {
            _writerAccessor.Writer = writer;
            await ExecuteRunAsync(input, writer, record, userMessage, callerId, observabilitySessionId, ct);
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
            _writerAccessor.Writer = null;
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
        AgUiMessage userMessage,
        string callerId,
        Guid observabilitySessionId,
        CancellationToken ct)
    {
        var userMessageText = userMessage.Content!;

        // Persist the user message under the client-supplied id when present so the optimistic
        // UI message and the server record share the same id. Retry/edit operations reference
        // this id, so minting a fresh one here would silently desync the client and break them.
        // Fall back to a server-generated id only when the client omits or sends an invalid id.
        var userMsg = new ConversationMessage(
            ParseClientId(userMessage.Id),
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
            ObservabilitySessionId = observabilitySessionId,
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

            // A provider-configuration failure carries an actionable, secret-free message. Surface it
            // to the developer in Development so the chat itself explains what to fix; keep the generic
            // message in Production to avoid leaking configuration detail.
            var message = result.ErrorKind == AgentTurnErrorKind.Configuration
                && _environment.IsDevelopment()
                && !string.IsNullOrWhiteSpace(result.Error)
                    ? result.Error!
                    : "The agent was unable to process your request.";

            await writer.WriteAsync(new RunErrorEvent(message), ct);
            return;
        }

        var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, record.AgentName);
        if (result.ToolsInvoked.Count > 0)
            OrchestrationMetrics.ToolCalls.Add(result.ToolsInvoked.Count, agentTag);

        UserActivityMetrics.Turns.Add(1,
            new KeyValuePair<string, object?>(UserConventions.UserId, callerId),
            new KeyValuePair<string, object?>(AgentConventions.Name, record.AgentName));

        var previousTelemetry = record.Telemetry ?? TelemetryAccumulator.Zero;
        var updatedTelemetry = previousTelemetry.Add(
            result.InputTokens, result.OutputTokens,
            result.CacheRead, result.CacheWrite,
            result.CostUsd, result.ToolsInvoked.Count);

        try
        {
            await _observabilityStore.UpdateSessionMetricsAsync(
                observabilitySessionId,
                updatedTelemetry.TurnCount, updatedTelemetry.ToolCallCount, subagentCount: 0,
                updatedTelemetry.InputTokens, updatedTelemetry.OutputTokens,
                updatedTelemetry.CacheRead, updatedTelemetry.CacheWrite,
                updatedTelemetry.CostUsd,
                Math.Round(updatedTelemetry.CacheHitRate, 4),
                result.Model, ct);

            await _conversationStore.UpdateTelemetryAsync(
                input.ThreadId, observabilitySessionId, updatedTelemetry, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AG-UI run {RunId}: failed to persist session metrics.", input.RunId);
        }

        // Stream and persist the assistant response under a single stable id. The client
        // references this id (via TEXT_MESSAGE_START) for retry-from-message, so the streamed
        // id and the persisted id MUST match. All TEXT_MESSAGE_* events for this message share it.
        var assistantId = Guid.NewGuid();
        var messageId = assistantId.ToString();
        await writer.WriteAsync(new TextMessageStartEvent(messageId, "assistant"), ct);

        var response = result.Response;
        for (var i = 0; i < response.Length; i += ChunkSize)
        {
            var chunk = response.Substring(i, Math.Min(ChunkSize, response.Length - i));
            await writer.WriteAsync(new TextMessageContentEvent(messageId, chunk), ct);
        }

        await writer.WriteAsync(new TextMessageEndEvent(messageId), ct);

        // Persist the assistant response under the same id that was streamed to the client.
        var assistantMsg = new ConversationMessage(
            assistantId,
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

    /// <summary>
    /// Parses a client-supplied message id into a <see cref="Guid"/>, falling back to a freshly
    /// generated id when the client omits it or sends a value that is not a valid GUID. Preserving
    /// the client id keeps the optimistic UI message and the persisted record in sync so that
    /// retry/edit operations (keyed by message id) resolve to a stored message.
    /// </summary>
    private static Guid ParseClientId(string? clientId) =>
        Guid.TryParse(clientId, out var parsed) && parsed != Guid.Empty
            ? parsed
            : Guid.NewGuid();

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

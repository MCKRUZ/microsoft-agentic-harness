using System.Diagnostics;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.AI.Common.Services;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Telemetry.Conventions;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
    private readonly ConversationLockRegistry _lockRegistry;
    private readonly ISessionHealthTracker _healthTracker;
    private readonly IObservabilityStore _observabilityStore;
    private readonly IConnectionTracker _connectionTracker;
    private readonly AgentHubConfig _config;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ConversationOrchestrator> _logger;

    public ConversationOrchestrator(
        IMediator mediator,
        IConversationStore conversationStore,
        ConversationLockRegistry lockRegistry,
        ISessionHealthTracker healthTracker,
        IObservabilityStore observabilityStore,
        IConnectionTracker connectionTracker,
        IOptions<AgentHubConfig> config,
        IHostEnvironment environment,
        ILogger<ConversationOrchestrator> logger)
    {
        _mediator = mediator;
        _conversationStore = conversationStore;
        _lockRegistry = lockRegistry;
        _healthTracker = healthTracker;
        _observabilityStore = observabilityStore;
        _connectionTracker = connectionTracker;
        _config = config.Value;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<(ConversationRecord Record, IReadOnlyList<ConversationMessage> History)> StartConversationAsync(
        string sessionKey, string agentName, string? conversationId, string callerId, CancellationToken ct)
    {
        var existing = await ValidateOwnershipAsync(conversationId, callerId, ct);

        ConversationRecord record;
        if (existing is null)
        {
            record = await _conversationStore.CreateAsync(
                agentName, callerId,
                conversationId: string.IsNullOrWhiteSpace(conversationId) ? null : conversationId,
                ct: ct);

            _logger.LogInformation("Created conversation {ConversationId} for user {UserId}.",
                record.Id, callerId);
        }
        else
        {
            record = existing;
        }

        var history = await _conversationStore.GetHistoryForDispatch(
            record.Id, _config.MaxHistoryMessages, ct) ?? [];

        return (record, history);
    }

    /// <inheritdoc />
    public async Task SetSettingsAsync(
        string conversationId, ConversationSettings settings, string callerId, CancellationToken ct)
    {
        _ = await ValidateOwnershipAsync(conversationId, callerId, ct)
            ?? throw new InvalidOperationException("Conversation not found.");

        var updated = await _conversationStore.UpdateSettingsAsync(conversationId, settings, ct)
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
        var record = await ValidateOwnershipAsync(conversationId, callerId, ct)
            ?? throw new InvalidOperationException("Conversation not found.");

        var semaphore = _lockRegistry.GetOrCreate(conversationId);
        await semaphore.WaitAsync(ct);
        try
        {
            var userMsg = new ConversationMessage(
                userMessageId == Guid.Empty ? Guid.NewGuid() : userMessageId,
                MessageRole.User, message, DateTimeOffset.UtcNow);
            await _conversationStore.AppendMessageAsync(conversationId, userMsg, ct);

            return await DispatchTurnAsync(sessionKey, conversationId, record.AgentName, message, callerId, onChunk, ct);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TurnOutcome> RetryFromMessageAsync(
        string sessionKey, string conversationId, Guid assistantMessageId, string callerId,
        Func<string, CancellationToken, Task>? onChunk, CancellationToken ct)
    {
        var record = await ValidateOwnershipAsync(conversationId, callerId, ct)
            ?? throw new InvalidOperationException("Conversation not found.");

        var semaphore = _lockRegistry.GetOrCreate(conversationId);
        await semaphore.WaitAsync(ct);
        try
        {
            var truncated = await _conversationStore.TruncateFromMessageAsync(conversationId, assistantMessageId, ct)
                ?? throw new InvalidOperationException("Conversation not found.");

            var last = truncated.Messages.LastOrDefault();
            if (last is null || last.Role != MessageRole.User)
                throw new InvalidOperationException("Cannot retry: no preceding user message found.");

            var outcome = await DispatchTurnAsync(
                sessionKey, conversationId, record.AgentName, last.Content, callerId, onChunk, ct);

            return outcome with { HistoryKeepCount = truncated.Messages.Count };
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TurnOutcome> EditAndResubmitAsync(
        string sessionKey, string conversationId, Guid userMessageId, Guid newUserMessageId,
        string newContent, string callerId,
        Func<string, CancellationToken, Task>? onChunk, CancellationToken ct)
    {
        var record = await ValidateOwnershipAsync(conversationId, callerId, ct)
            ?? throw new InvalidOperationException("Conversation not found.");

        var semaphore = _lockRegistry.GetOrCreate(conversationId);
        await semaphore.WaitAsync(ct);
        try
        {
            var truncated = await _conversationStore.TruncateFromMessageAsync(conversationId, userMessageId, ct)
                ?? throw new InvalidOperationException("Conversation not found.");

            var newUserMsg = new ConversationMessage(
                newUserMessageId == Guid.Empty ? Guid.NewGuid() : newUserMessageId,
                MessageRole.User, newContent, DateTimeOffset.UtcNow);
            await _conversationStore.AppendMessageAsync(conversationId, newUserMsg, ct);

            var outcome = await DispatchTurnAsync(
                sessionKey, conversationId, record.AgentName, newContent, callerId, onChunk, ct);

            return outcome with { HistoryKeepCount = truncated.Messages.Count };
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task ValidateAccessAsync(string conversationId, string callerId, CancellationToken ct)
    {
        var record = await ValidateOwnershipAsync(conversationId, callerId, ct);
        if (record is null)
            throw new InvalidOperationException("Conversation not found.");
    }

    /// <inheritdoc />
    public async Task HandleDisconnectAsync(string sessionKey, Exception? exception, CancellationToken ct)
    {
        var info = _connectionTracker.Untrack(sessionKey);
        if (info is null) return;

        SessionMetrics.ActiveSessions.Add(-1, new TagList { { AgentConventions.Name, info.AgentName } });

        if (info.TurnCount > 0)
        {
            var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, info.AgentName);
            var elapsed = DateTimeOffset.UtcNow - info.StartedAt;
            OrchestrationMetrics.ConversationDuration.Record(elapsed.TotalMilliseconds, agentTag);
            OrchestrationMetrics.TurnsPerConversation.Record(info.TurnCount, agentTag);
        }

        var status = exception is null ? "completed" : "errored";
        try
        {
            await _observabilityStore.EndSessionAsync(
                info.ObservabilitySessionId, status, exception?.Message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to end observability session {SessionId}", info.ObservabilitySessionId);
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<TurnOutcome> DispatchTurnAsync(
        string sessionKey, string conversationId, string agentName, string userMessage,
        string callerId, Func<string, CancellationToken, Task>? onChunk, CancellationToken ct)
    {
        Activity.Current?.SetTag("agent.conversation_id", conversationId);
        Activity.Current?.SetTag(AgentConventions.Name, agentName);
        Activity.Current?.SetTag(UserConventions.UserId, callerId);
        Activity.Current?.AddBaggage("agent.conversation_id", conversationId);
        Activity.Current?.AddBaggage(UserConventions.UserId, callerId);

        await EnsureSessionTrackedAsync(sessionKey, conversationId, agentName, callerId, ct);

        var history = await _conversationStore.GetHistoryForDispatch(
            conversationId, _config.MaxHistoryMessages, ct) ?? [];

        var updatedRecord = await _conversationStore.GetAsync(conversationId, ct);
        var turnNumber = updatedRecord?.Messages.Count ?? 0;

        var obsSessionId = _connectionTracker.Get(sessionKey)?.ObservabilitySessionId ?? Guid.Empty;

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
            return await HandleTurnErrorAsync(conversationId, ex, kind, ct);
        }
        finally
        {
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
            return await HandleTurnErrorAsync(conversationId,
                new InvalidOperationException(result.Error ?? "Agent returned a failure result."),
                result.ErrorKind, ct);
        }

        var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, agentName);
        if (result.ToolsInvoked.Count > 0)
            OrchestrationMetrics.ToolCalls.Add(result.ToolsInvoked.Count, agentTag);

        _healthTracker.RecordSuccess(agentName);

        var userTag = new KeyValuePair<string, object?>(UserConventions.UserId, callerId);
        var userAgentTag = new KeyValuePair<string, object?>(AgentConventions.Name, agentName);
        UserActivityMetrics.Turns.Add(1, userTag, userAgentTag);

        await UpdateSessionMetricsAsync(sessionKey, result);

        // Token deltas were already streamed to the caller during dispatch via the
        // ambient AgentTurnStreamSink. The final authoritative text rides TurnComplete.
        var assistantMessageId = Guid.NewGuid();
        var assistantMsg = new ConversationMessage(
            assistantMessageId, MessageRole.Assistant, result.Response, DateTimeOffset.UtcNow);
        await _conversationStore.AppendMessageAsync(conversationId, assistantMsg, ct);

        var finalRecord = await _conversationStore.GetAsync(conversationId, ct);
        var finalTurnNumber = finalRecord?.Messages.Count ?? turnNumber + 1;

        return new TurnOutcome
        {
            Success = true,
            Response = result.Response,
            AssistantMessageId = assistantMessageId,
            FinalTurnNumber = finalTurnNumber,
        };
    }

    private async Task EnsureSessionTrackedAsync(
        string sessionKey, string conversationId, string agentName, string callerId, CancellationToken ct)
    {
        var tracked = _connectionTracker.Get(sessionKey);
        if (tracked?.ConversationId == conversationId)
            return;

        if (tracked is not null)
        {
            SessionMetrics.ActiveSessions.Add(-1, new TagList { { AgentConventions.Name, tracked.AgentName } });
            await _observabilityStore.EndSessionAsync(tracked.ObservabilitySessionId, "completed", cancellationToken: ct);
        }

        var newSessionId = await _observabilityStore.StartSessionAsync(
            conversationId, agentName, model: null, ct);

        if (newSessionId == Guid.Empty)
            _logger.LogWarning("StartSessionAsync returned empty GUID for conversation {ConversationId}", conversationId);

        _connectionTracker.Track(sessionKey, new ActiveConversationInfo(
            conversationId, agentName, callerId, DateTimeOffset.UtcNow, 0, newSessionId));

        SessionMetrics.ActiveSessions.Add(1, new TagList { { AgentConventions.Name, agentName } });
        SessionMetrics.SessionsStarted.Add(1, new KeyValuePair<string, object?>(AgentConventions.Name, agentName));
        UserActivityMetrics.SessionsStarted.Add(1,
            new KeyValuePair<string, object?>(UserConventions.UserId, callerId));
    }

    private async Task UpdateSessionMetricsAsync(string sessionKey, AgentTurnResult result)
    {
        var convInfo = _connectionTracker.Get(sessionKey);
        if (convInfo is null) return;

        var updated = convInfo with
        {
            TurnCount = convInfo.TurnCount + 1,
            LastActivityAt = DateTimeOffset.UtcNow,
            ToolCallCount = convInfo.ToolCallCount + result.ToolsInvoked.Count,
            TotalInputTokens = convInfo.TotalInputTokens + result.InputTokens,
            TotalOutputTokens = convInfo.TotalOutputTokens + result.OutputTokens,
            TotalCacheRead = convInfo.TotalCacheRead + result.CacheRead,
            TotalCacheWrite = convInfo.TotalCacheWrite + result.CacheWrite,
            TotalCostUsd = convInfo.TotalCostUsd + result.CostUsd,
        };
        _connectionTracker.Track(sessionKey, updated);

        try
        {
            await _observabilityStore.UpdateSessionMetricsAsync(
                updated.ObservabilitySessionId,
                updated.TurnCount, updated.ToolCallCount, subagentCount: 0,
                updated.TotalInputTokens, updated.TotalOutputTokens,
                updated.TotalCacheRead, updated.TotalCacheWrite,
                updated.TotalCostUsd,
                updated.TotalInputTokens > 0
                    ? (decimal)updated.TotalCacheRead / updated.TotalInputTokens
                    : 0m,
                result.Model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to persist session metrics for session {SessionId}", updated.ObservabilitySessionId);
        }
    }

    private async Task<TurnOutcome> HandleTurnErrorAsync(
        string conversationId, Exception ex, AgentTurnErrorKind errorKind, CancellationToken ct)
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
            await _conversationStore.AppendMessageAsync(conversationId, errorMsg, ct);
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
    /// Returns the conversation record if it exists, or null if it doesn't.
    /// Throws <see cref="UnauthorizedAccessException"/> if the record exists but belongs to a different user.
    /// </summary>
    private async Task<ConversationRecord?> ValidateOwnershipAsync(
        string? conversationId, string callerId, CancellationToken ct)
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
            throw new UnauthorizedAccessException("Access denied.");
        }

        return record;
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
}

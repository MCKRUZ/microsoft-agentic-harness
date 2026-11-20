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
/// <remarks>
/// Split into partials by responsibility once this file passed the project's file-size
/// convention — <see cref="ReassignAgentAsync"/> and its lease/cache-atomicity remarks live in
/// <c>ConversationOrchestrator.ReassignAgent.cs</c>.
/// </remarks>
public sealed partial class ConversationOrchestrator : IConversationOrchestrator
{
    private readonly IMediator _mediator;
    private readonly IConversationStore _conversationStore;
    private readonly IConversationTurnLease _turnLease;
    private readonly IAgentConversationCache _agentCache;
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
        IAgentConversationCache agentCache,
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
        _agentCache = agentCache;
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
        // Same shape as ReassignAgentAsync: a header-only ownership check so a foreign caller never holds
        // the lease, then write and invalidate under it — a turn re-reads the record under the lease, so it
        // sees the old settings or the new ones, never a stale agent. The cached agent bakes its settings in
        // and a hit never re-reads them (#786); invalidated rather than evicted so unlocked skills survive.
        // Since #788 the cache also notices changed build inputs on its own, so this no longer decides
        // correctness; it is kept so the superseded agent's execution trace is finalised now, under the
        // lease, rather than whenever the next turn happens to arrive.
        _ = await _conversationStore.GetAgentNameAsync(conversationId, callerId, ct)
            ?? throw new InvalidOperationException("Conversation not found.");

        var updated = await WithTurnLeaseAsync(conversationId, async leased =>
        {
            var record = await _conversationStore.UpdateSettingsAsync(
                    conversationId, callerId, settings, leased.Token)
                ?? throw new InvalidOperationException("Conversation not found.");

            _agentCache.Invalidate(conversationId);
            return record;
        }, ct);

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
        // Existence/ownership check only -- DispatchTurnAsync resolves its own dispatch agent fresh,
        // under the lease, rather than trusting whatever this pre-lease read saw.
        _ = await _conversationStore.GetAsync(conversationId, callerId, ct)
            ?? throw new InvalidOperationException("Conversation not found.");

        return await WithTurnLeaseAsync(conversationId, async leased =>
        {
            var userMsg = new ConversationMessage(
                userMessageId == Guid.Empty ? Guid.NewGuid() : userMessageId,
                MessageRole.User, message, DateTimeOffset.UtcNow);

            // Stored once the turn is admitted, not here: see AgentTurnAdmission (#964).
            var pending = new PendingTurn(
                message, NewMessageId: userMsg.Id, ReplacesFromId: null, RetriedMessageId: null,
                OnAdmitted: admitCt => _conversationStore.AppendMessageAsync(conversationId, callerId, userMsg, admitCt));

            return await DispatchTurnAsync(sessionKey, conversationId, pending, callerId, onChunk, leased, leased.Token);
        }, ct);
    }

    /// <summary>
    /// Invokes <paramref name="onHistoryTruncated"/>, if provided, with the surviving message
    /// count — swallowing (and logging) any exception it throws, a cancellation included.
    /// </summary>
    /// <remarks>
    /// The truncation this signals has already committed durably in <see cref="_conversationStore"/>
    /// by the time this runs, inside the turn's admission (#964). An exception escaping from here
    /// would fail the admission and report the turn as never admitted, so the client would be told to
    /// drop a message the store already changed. A disconnect still ends the turn: the turn's own token
    /// is cancelled, and the run stops on it after admission. The notice is sent without that token
    /// so a disconnect cannot interrupt it halfway. See #328 for why it fires before dispatch.
    /// </remarks>
    private async Task SignalHistoryTruncatedAsync(Func<int, CancellationToken, Task>? onHistoryTruncated, int keepCount)
    {
        if (onHistoryTruncated is null) return;

        try
        {
            await onHistoryTruncated(keepCount, CancellationToken.None);
        }
        catch (Exception ex)
        {
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
        // Existence/ownership check only -- DispatchTurnAsync resolves its own dispatch agent fresh,
        // under the lease, rather than trusting whatever this pre-lease read saw.
        _ = await _conversationStore.GetAsync(conversationId, callerId, ct)
            ?? throw new InvalidOperationException("Conversation not found.");

        return await WithTurnLeaseAsync(conversationId, async leased =>
        {
            var record = await _conversationStore.GetAsync(conversationId, callerId, leased.Token)
                ?? throw new InvalidOperationException("Conversation not found.");
            var retried = PrecedingUserMessage(record.Messages, assistantMessageId)
                ?? throw new InvalidOperationException(ConversationRetryNotice.NoPrecedingUserMessage);

            // The old answer is dropped once the turn is admitted, not before: a retry refused by screening keeps it
            // (#964, the retry sibling of #962). The client is told at that moment, before this turn's deltas (#328).
            int? keptOnAdmission = null;
            var pending = new PendingTurn(
                retried.Content, NewMessageId: null, ReplacesFromId: retried.Id, RetriedMessageId: retried.Id,
                OnAdmitted: async admitCt =>
                {
                    var truncated = await _conversationStore.TruncateFromMessageAsync(
                            conversationId, callerId, assistantMessageId, admitCt)
                        ?? throw new InvalidOperationException("Conversation not found.");
                    keptOnAdmission = truncated.Messages.Count;
                    await SignalHistoryTruncatedAsync(onHistoryTruncated, truncated.Messages.Count);
                },
                OnHistoryTruncated: onHistoryTruncated);

            var outcome = await DispatchTurnAsync(sessionKey, conversationId, pending, callerId, onChunk, leased, leased.Token);
            return keptOnAdmission is { } kept ? outcome with { HistoryKeepCount = kept } : outcome;
        }, ct);
    }

    /// <inheritdoc />
    public async Task<TurnOutcome> EditAndResubmitAsync(
        string sessionKey, string conversationId, Guid userMessageId, Guid newUserMessageId,
        string newContent, string callerId,
        Func<string, CancellationToken, Task>? onChunk, CancellationToken ct,
        Func<int, CancellationToken, Task>? onHistoryTruncated = null)
    {
        // Existence/ownership check only -- DispatchTurnAsync resolves its own dispatch agent fresh,
        // under the lease, rather than trusting whatever this pre-lease read saw.
        _ = await _conversationStore.GetAsync(conversationId, callerId, ct)
            ?? throw new InvalidOperationException("Conversation not found.");

        return await WithTurnLeaseAsync(conversationId, async leased =>
        {
            var newUserMsg = new ConversationMessage(
                newUserMessageId == Guid.Empty ? Guid.NewGuid() : newUserMessageId,
                MessageRole.User, newContent, DateTimeOffset.UtcNow);

            // The original and everything after it are replaced once the turn is admitted, not before: an edit refused
            // by screening, a verdict or an outage, leaves the conversation as it was (#962, #964).
            int? keptOnAdmission = null;
            var pending = new PendingTurn(
                newContent, NewMessageId: newUserMsg.Id, ReplacesFromId: userMessageId, RetriedMessageId: null,
                OnAdmitted: async admitCt =>
                {
                    var truncated = await _conversationStore.TruncateFromMessageAsync(
                            conversationId, callerId, userMessageId, admitCt)
                        ?? throw new InvalidOperationException("Conversation not found.");

                    // Not cancellable once the original is gone: a disconnect here must not leave the conversation
                    // with neither the original nor the edit.
                    await _conversationStore.AppendMessageAsync(conversationId, callerId, newUserMsg, CancellationToken.None);
                    keptOnAdmission = truncated.Messages.Count;

                    // After the edit is stored: the client re-inserts the edit on this notice, so sending it first could
                    // leave the client showing an edit the server never stored. Before this turn's deltas (#328).
                    await SignalHistoryTruncatedAsync(onHistoryTruncated, truncated.Messages.Count);
                });

            var outcome = await DispatchTurnAsync(sessionKey, conversationId, pending, callerId, onChunk, leased, leased.Token);
            return keptOnAdmission is { } kept ? outcome with { HistoryKeepCount = kept } : outcome;
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
}

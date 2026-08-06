using System.Diagnostics;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.AI;

/// <summary>
/// The one implementation of <see cref="IConversationTelemetryRecorder"/>: the policy all three
/// transports used to carry a copy of.
/// </summary>
/// <remarks>
/// Reads and writes two stores. <see cref="IObservabilityStore"/> holds the dashboard-facing session
/// rollup, keyed one row per conversation and written with SET semantics; <see cref="IConversationStore"/>
/// holds the same totals on the conversation record, which is what makes them survive a process, a
/// reconnect, or a switch of transport. Both are written from the same value on every turn, so they
/// cannot disagree.
/// </remarks>
public sealed class ConversationTelemetryRecorder : IConversationTelemetryRecorder
{
    private readonly IObservabilityStore _observabilityStore;
    private readonly IConversationStore _conversationStore;
    private readonly ILogger<ConversationTelemetryRecorder> _logger;

    /// <summary>Initializes the recorder.</summary>
    /// <param name="observabilityStore">Receives the dashboard-facing session rollup.</param>
    /// <param name="conversationStore">Holds the durable copy of the same totals.</param>
    /// <param name="logger">Receives the warnings emitted when a telemetry write fails.</param>
    public ConversationTelemetryRecorder(
        IObservabilityStore observabilityStore,
        IConversationStore conversationStore,
        ILogger<ConversationTelemetryRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(observabilityStore);
        ArgumentNullException.ThrowIfNull(conversationStore);
        ArgumentNullException.ThrowIfNull(logger);

        _observabilityStore = observabilityStore;
        _conversationStore = conversationStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ConversationTelemetryState> BeginAsync(
        string conversationId,
        string? callerId,
        string agentName,
        ConversationRecord? knownRecord = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        // Blank is refused rather than coerced to null. An absent caller is a run with no transcript; a
        // blank one is a bug upstream, and treating it as "nobody in particular, carry on" is how an
        // empty identity has been read as "everyone" in this codebase before.
        if (callerId is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(callerId);

        (Guid SessionId, TelemetryAccumulator Totals) existing;
        if (callerId is null)
        {
            // No owner means no durable record to read: a self-contained run starts from nothing and
            // keeps its totals only for the length of the run.
            existing = (Guid.Empty, TelemetryAccumulator.Zero);
        }
        else if (knownRecord is not null)
        {
            // Checked rather than trusted. A record for a different conversation, or one read under a
            // different identity, would have this adopt the wrong session and then persist totals under
            // the caller supplied here — an identity-shaped assumption, which is the shape of mistake
            // this codebase has paid for before.
            if (!string.Equals(knownRecord.Id, conversationId, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"The supplied record is for conversation '{knownRecord.Id}', not '{conversationId}'.",
                    nameof(knownRecord));

            if (!string.Equals(knownRecord.UserId, callerId, StringComparison.Ordinal))
                throw new ArgumentException(
                    "The supplied record is owned by someone other than the caller.", nameof(knownRecord));

            existing = (knownRecord.ObservabilitySessionId ?? Guid.Empty,
                        knownRecord.Telemetry ?? TelemetryAccumulator.Zero);
        }
        else
        {
            existing = await LoadAsync(conversationId, callerId, cancellationToken);
        }

        if (existing.SessionId != Guid.Empty)
        {
            return new ConversationTelemetryState(
                conversationId, callerId, existing.SessionId, existing.Totals, SessionOpened: false);
        }

        var sessionId = await _observabilityStore.StartSessionAsync(
            conversationId, agentName, model: null, cancellationToken);

        SessionMetrics.SessionsStarted.Add(1, new KeyValuePair<string, object?>(AgentConventions.Name, agentName));

        // User activity needs a user. Previously two of the three transports emitted this and the third
        // did not, for no reason anyone recorded — the difference was that the third had not been given
        // an identity to attribute it to, which is now the actual condition rather than an accident of
        // which file the code lived in.
        if (callerId is not null)
        {
            UserActivityMetrics.SessionsStarted.Add(
                1, new KeyValuePair<string, object?>(UserConventions.UserId, callerId));

            // Written immediately, before the first turn, so a crash in between does not leave the
            // conversation opening a second session next time and restamping its start time.
            await PersistAsync(conversationId, callerId, sessionId, existing.Totals, cancellationToken);
        }

        return new ConversationTelemetryState(
            conversationId, callerId, sessionId, existing.Totals, SessionOpened: true);
    }

    /// <inheritdoc />
    public async Task<ConversationTelemetryState> RecordTurnAsync(
        ConversationTelemetryState state,
        ConversationTurnTelemetry turn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(turn);

        var totals = state.Totals.Add(
            turn.InputTokens, turn.OutputTokens, turn.CacheRead, turn.CacheWrite, turn.CostUsd, turn.ToolCalls);

        var updated = state with { Totals = totals };

        try
        {
            await _observabilityStore.UpdateSessionMetricsAsync(
                state.SessionId,
                totals.TurnCount,
                totals.ToolCallCount,
                subagentCount: 0,
                totals.InputTokens,
                totals.OutputTokens,
                totals.CacheRead,
                totals.CacheWrite,
                totals.CostUsd,
                Math.Round(totals.CacheHitRate, 4),
                turn.Model,
                cancellationToken);

            if (state.CallerId is not null)
                await PersistAsync(state.ConversationId, state.CallerId, state.SessionId, totals, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The turn has already happened and been paid for. Failing it now to report an accounting
            // problem would cost the caller the work as well as the record. The returned state still
            // carries the new totals, so the next turn continues from the right place and the next write
            // catches the row up.
            _logger.LogWarning(
                ex,
                "Failed to record turn {TurnNumber} telemetry for conversation {ConversationId}; the "
                    + "rollup is behind by this turn until the next write succeeds",
                totals.TurnCount,
                state.ConversationId);
        }

        return updated;
    }

    private async Task<(Guid SessionId, TelemetryAccumulator Totals)> LoadAsync(
        string conversationId, string callerId, CancellationToken cancellationToken)
    {
        var record = await _conversationStore.GetAsync(conversationId, callerId, cancellationToken);
        return (record?.ObservabilitySessionId ?? Guid.Empty, record?.Telemetry ?? TelemetryAccumulator.Zero);
    }

    private async Task PersistAsync(
        string conversationId,
        string callerId,
        Guid sessionId,
        TelemetryAccumulator totals,
        CancellationToken cancellationToken)
    {
        await _conversationStore.UpdateTelemetryAsync(
            conversationId, callerId, sessionId, totals, cancellationToken);
    }
}

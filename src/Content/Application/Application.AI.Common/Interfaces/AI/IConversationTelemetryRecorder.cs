using Application.AI.Common.Models.Conversations;

namespace Application.AI.Common.Interfaces.AI;

/// <summary>
/// Records what a conversation spends, once, for every transport that runs one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> Three transports — the CQRS/bundle handler, the AG-UI HTTP
/// endpoint, and the SignalR hub — each implemented the same policy independently, and had already
/// drifted four ways: whether an existing session is adopted or a new one always opened, where the
/// running totals are read from, how the cache hit rate is computed, and which counters are
/// incremented. The observability row is keyed one-per-conversation and written with SET semantics, so
/// two of those differences were not cosmetic: they overwrote the conversation's rollup with one
/// transport's partial view (issues #255, #280).
/// </para>
/// <para>
/// <strong>What it deliberately does not own: ending the session.</strong> That rule genuinely differs
/// per transport and is not duplication — a bundle run ends the session it opened, a stateless HTTP
/// request leaves the conversation's session open for the next one, and the hub ends it when the
/// connection drops. Folding those into one method would mean a flag that selects between three
/// behaviours, which is the same three implementations with extra steps.
/// </para>
/// </remarks>
public interface IConversationTelemetryRecorder
{
    /// <summary>
    /// Finds where a conversation has got to, opening an observability session for it if it has none.
    /// </summary>
    /// <param name="conversationId">The conversation about to take a turn.</param>
    /// <param name="callerId">
    /// The authenticated owner, or <see langword="null"/> for a run with no durable transcript — see
    /// <see cref="ConversationTelemetryState.CallerId"/>. A blank string is rejected rather than treated
    /// as absent, because an empty identity has been read as "everyone" in this codebase before.
    /// </param>
    /// <param name="agentName">The agent about to run, recorded on a session this call opens.</param>
    /// <param name="knownRecord">
    /// The conversation record, when the caller has already read it. Supplying it avoids a second read
    /// of the same row — and, for a caller that reads under a lease or in a deliberate sequence, avoids
    /// this call quietly inserting a read into the middle of that sequence. Pass
    /// <see langword="null"/> to have the record fetched.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The conversation's session and running totals.</returns>
    /// <remarks>
    /// Adopt-or-open, never open-unconditionally. A conversation has one session row for its whole life;
    /// opening a second restamps its start time and every duration derived from it.
    /// </remarks>
    Task<ConversationTelemetryState> BeginAsync(
        string conversationId,
        string? callerId,
        string agentName,
        ConversationRecord? knownRecord = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a turn to the conversation's totals and writes the new totals to both stores.
    /// </summary>
    /// <param name="state">Where the conversation had got to, from <see cref="BeginAsync"/> or the previous turn.</param>
    /// <param name="turn">What this turn cost.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The updated state. Pass it to the next turn.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Cumulative, not per-run.</strong> The observability row is written with SET semantics, so
    /// what goes into it has to be the conversation's whole total. Writing a run's own share there is
    /// the overwrite this interface exists to make impossible to spell.
    /// </para>
    /// <para>
    /// Never throws for a store failure. Telemetry that cannot be written is worth a log line, not a
    /// failed turn the caller has already paid for.
    /// </para>
    /// </remarks>
    Task<ConversationTelemetryState> RecordTurnAsync(
        ConversationTelemetryState state,
        ConversationTurnTelemetry turn,
        CancellationToken cancellationToken = default);
}

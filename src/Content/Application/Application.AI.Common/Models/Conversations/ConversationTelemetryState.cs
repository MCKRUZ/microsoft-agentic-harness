namespace Application.AI.Common.Models.Conversations;

/// <summary>
/// A conversation's telemetry position: which observability session it is being recorded against, and
/// everything it has spent so far. Handed out by <c>IConversationTelemetryRecorder.BeginAsync</c> and
/// handed back to it after every turn.
/// </summary>
/// <param name="ConversationId">The conversation these totals belong to.</param>
/// <param name="CallerId">
/// The authenticated owner, or <see langword="null"/> for a run with no durable transcript. Null means
/// the totals are not written back to any conversation record — there is none — so they live only for
/// the length of the run. It is never a wildcard: the store refuses a blank caller outright, and this
/// type does not offer one.
/// </param>
/// <param name="SessionId">The observability session row this conversation's rollup is written to.</param>
/// <param name="Totals">Everything the conversation has spent, across every run and every transport.</param>
/// <param name="SessionOpened">
/// Whether this call opened the session rather than adopting one the conversation already had. The
/// recorder knows; a caller re-deriving it by testing the record it passed in is the same question
/// answered twice, and the two answers can disagree once anything else writes a session id.
/// </param>
/// <remarks>
/// <para>
/// The point of carrying this as one value is that the three transports used to each keep their own
/// idea of it, and drifted. The SignalR path kept totals on a per-<em>connection</em> object, so
/// reconnecting reset the conversation's rollup to whatever the new connection had spent (issue #280).
/// A conversation's totals are a property of the conversation, and this is the shape that says so.
/// </para>
/// </remarks>
public sealed record ConversationTelemetryState(
    string ConversationId,
    string? CallerId,
    Guid SessionId,
    TelemetryAccumulator Totals,
    bool SessionOpened = false)
{
    /// <summary>
    /// The number to give the next turn: one past however many the conversation has already taken.
    /// </summary>
    /// <remarks>
    /// Derived from the totals rather than from a message count or a per-connection counter, both of
    /// which have been used before and both of which produce a different sequence for the same
    /// conversation depending on how it was reached — a message count advances by two per turn.
    /// </remarks>
    public int NextTurnNumber => Totals.TurnCount + 1;
}

using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Services;

namespace Application.AI.Common.Extensions;

/// <summary>
/// Records a turn that did not complete — the failure and cancellation counterpart of
/// <see cref="IConversationTelemetryRecorder.RecordTurnAsync"/> (#780).
/// </summary>
/// <remarks>
/// A turn that fails after its model calls have been paid for spent real money, and the conversation
/// budget is charged for it (#778). Without this, the session rollup the dashboards read stayed behind
/// the budget gate's own figure, so a conversation could be stopped for exceeding a ceiling the
/// dashboard said it was under.
/// </remarks>
public static class ConversationTelemetryRecorderExtensions
{
    /// <summary>
    /// Adds a failed or cancelled turn to the conversation's totals, when it spent anything.
    /// </summary>
    /// <param name="recorder">The shared recorder.</param>
    /// <param name="state">Where the conversation had got to before this turn.</param>
    /// <param name="turn">What the failed turn cost.</param>
    /// <param name="leased">
    /// The lease the turn ran under, asked whether it was lost; <see langword="null"/> for a run that holds
    /// none (a self-contained run has no durable conversation to lose).
    /// </param>
    /// <returns>
    /// The updated state, counting the turn. <paramref name="state"/> itself, untouched, when the turn
    /// spent nothing or when the lease was lost.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>A turn that spent nothing is not recorded.</strong> A failure before any model call (a
    /// missing provider configuration, say) has no cost to report, and counting it as a turn would make
    /// the rollup's turn count move on configuration errors that have never moved it.
    /// </para>
    /// <para>
    /// <strong>Never under the caller's token.</strong> A disconnecting client cancels the token the turn
    /// ran under, and that token on the write would drop the record of exactly the turn that was cut
    /// short — the same reason the budget charge does not take one.
    /// </para>
    /// <para>
    /// <strong>Not at all once the lease is lost.</strong> That is the one cancellation that must stop the
    /// write: the totals are written absolute (SET semantics) from this run's baseline, so a host that no
    /// longer holds the conversation would overwrite what the host that does hold it has since recorded.
    /// The budget charge is an increment and is safe to make; this is not. The failed turn's spend stays
    /// in the budget and is simply not added to the rollup.
    /// </para>
    /// </remarks>
    public static Task<ConversationTelemetryState> RecordFailedTurnAsync(
        this IConversationTelemetryRecorder recorder,
        ConversationTelemetryState state,
        ConversationTurnTelemetry turn,
        LeasedTurn? leased)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(turn);

        return turn.HasSpend && leased?.LeaseWasLost != true
            ? recorder.RecordTurnAsync(state, turn, CancellationToken.None)
            : Task.FromResult(state);
    }
}

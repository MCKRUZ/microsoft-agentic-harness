using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.MediatR;

namespace Application.AI.Common.Extensions;

/// <summary>
/// Charges a finished turn to the conversation-lifetime budget — for every way a turn can end.
/// </summary>
/// <remarks>
/// <para>
/// Three transports each wrote this charge out by hand, on the success path only, so a turn that
/// failed or was cancelled after its model calls had already been paid for charged nothing (#778).
/// Putting the formula and the cancellation rule in one place is what stops a fourth transport, or a
/// future edit to one of these, from reintroducing that.
/// </para>
/// <para>
/// <strong>Never under the caller's token.</strong> A client that disconnects cancels the token the turn
/// ran under, and that same cancelled token would make the accrual throw — losing the spend of exactly
/// the turn that was cut short. The spend has already happened, so recording it is not something the
/// caller's patience can veto. (A durable tracker's write is a single small statement; the cost of not
/// cancelling it is negligible.)
/// </para>
/// </remarks>
public static class ConversationBudgetTrackerExtensions
{
    /// <summary>
    /// Adds <paramref name="result"/>'s input and output tokens to <paramref name="budgetKey"/>'s running
    /// total. A turn that spent nothing is a no-op.
    /// </summary>
    /// <param name="tracker">The conversation budget being charged.</param>
    /// <param name="budgetKey">The opaque budget key the turn belongs to.</param>
    /// <param name="result">The turn's outcome, success or not — whatever it reports was spent.</param>
    public static Task RecordTurnUsageAsync(
        this IConversationBudgetTracker tracker, string budgetKey, IAgentTurnResult result)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(result);

        return tracker.RecordUsageAsync(
            budgetKey, result.InputTokens + result.OutputTokens, CancellationToken.None);
    }
}

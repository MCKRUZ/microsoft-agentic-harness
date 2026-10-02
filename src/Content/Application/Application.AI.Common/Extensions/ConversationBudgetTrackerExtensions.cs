using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Extensions;

/// <summary>
/// Charges a finished turn to the conversation-lifetime budget — for every way a turn can end.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never under the caller's token.</strong> A disconnecting client cancels the token the turn
/// ran under, and that token on the write would abandon the spend of exactly the turn that was cut short.
/// </para>
/// <para>
/// <strong>An accrual error never replaces a failed turn's outcome.</strong> The caller has something
/// specific to do with a failure or cancellation, so a tracker error there is logged and the outcome
/// stands. For a successful turn the error propagates.
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
    /// <param name="logger">Where an accrual error on a non-successful turn is reported.</param>
    public static async Task RecordTurnUsageAsync(
        this IConversationBudgetTracker tracker, string budgetKey, IAgentTurnResult result, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(logger);

        var tokens = result.InputTokens + result.OutputTokens;

        try
        {
            await tracker.RecordUsageAsync(budgetKey, tokens, CancellationToken.None);
        }
        catch (Exception ex) when (!result.Success)
        {
            logger.LogError(ex,
                "Could not charge {Tokens} tokens from a turn that did not complete to the budget for {BudgetKey}.",
                tokens, budgetKey);
        }
    }
}

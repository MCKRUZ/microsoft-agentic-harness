using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Combines the built-in tool governor with the consumer observer chain into the single verdict a
/// caller should act on, for call sites that run no other gate in between.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> <see cref="IToolInvocationGovernor"/>'s allow is not the final
/// word — a host may register <see cref="IToolCallObserver"/> rules that run after it, and a call site
/// that acts on the governor's verdict alone silently bypasses every one of them. That defect shipped
/// on three separate execution paths before it was caught, each time because the author reasonably
/// assumed authorizing through the governor was the whole check. This method makes "authorize, then
/// observe" the one thing a caller has to remember, and puts the ordering in a single place.
/// </para>
/// <para>
/// <strong>Not for the agent's live tool path.</strong> <c>GovernedAIFunction</c> and
/// <c>DirectToolInvoker</c> deliberately interleave the classification gate — and, on the agent path,
/// the progress guard — between these two stages, so they call each one directly and in their own
/// order. Use this only where nothing runs in between, which today means the plan step executors.
/// </para>
/// </remarks>
public static class ToolAuthorizationExtensions
{
    /// <summary>
    /// Authorizes a tool or capability through the governor and, if that succeeds, through the host's
    /// observer chain. Returns the first refusal, or the governor's allow when both permit the call.
    /// </summary>
    /// <param name="governor">The built-in governance chokepoint. Runs first.</param>
    /// <param name="observers">
    /// The host's observer chain, or null where none is available. Consulted only for a call the
    /// governor already allowed, so an observer can tighten the outcome but never widen access.
    /// </param>
    /// <param name="toolName">The tool or plan capability being authorized.</param>
    /// <param name="arguments">
    /// The concrete call arguments, when the caller has them. They let an approval verdict describe
    /// the specific invocation to a human, let argument-conditioned policy rules match, and are what
    /// an observer inspects to make an argument-sensitive decision. Omitting them where they exist
    /// does not fail closed — it silently narrows every one of those checks to the tool name.
    /// </param>
    /// <param name="cancellationToken">Cancels the authorization, including any pending approval.</param>
    public static async ValueTask<ToolInvocationDecision> AuthorizeWithObserversAsync(
        this IToolInvocationGovernor governor,
        IToolCallObserverChain? observers,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(governor);

        var decision = await governor
            .AuthorizeAsync(toolName, cancellationToken, arguments)
            .ConfigureAwait(false);

        if (!decision.IsAllowed)
            return decision;

        if (observers is not { HasObservers: true })
            return decision;

        return await observers
            .EvaluateAsync(toolName, arguments ?? EmptyArguments, cancellationToken)
            .ConfigureAwait(false);
    }

    // An observer is always handed a dictionary, never null, so a rule can read arguments without a
    // null check. "The caller had no arguments" and "the call had none" are the same thing to a rule.
    private static readonly IReadOnlyDictionary<string, object?> EmptyArguments =
        new Dictionary<string, object?>();
}

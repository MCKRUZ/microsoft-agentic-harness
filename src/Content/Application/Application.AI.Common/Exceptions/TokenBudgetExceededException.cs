using Application.Common.Exceptions;

namespace Application.AI.Common.Exceptions;

/// <summary>
/// Thrown when a MediatR request would exceed the remaining token budget for the current
/// execution context.
/// </summary>
/// <remarks>
/// <para>
/// Raised by <see cref="Application.AI.Common.MediatRBehaviors.TokenBudgetBehavior{TRequest,TResponse}"/>
/// when <see cref="Application.AI.Common.Interfaces.AI.ITokenBudgetTracker.CanAfford"/> returns
/// <see langword="false"/> for a request implementing
/// <see cref="Application.AI.Common.Interfaces.MediatR.IConsumesTokens"/>.
/// </para>
/// <para>
/// Callers should catch this exception at the orchestration loop boundary and decide whether
/// to compact context, escalate to a human gate, or terminate the current plan step.
/// </para>
/// </remarks>
public sealed class TokenBudgetExceededException : ApplicationExceptionBase
{
    /// <summary>Gets the remaining token budget at the time of the rejection.</summary>
    public int RemainingBudget { get; }

    /// <summary>Gets the estimated token cost of the rejected request.</summary>
    public int RequestedTokens { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="TokenBudgetExceededException"/> with
    /// structured budget context.
    /// </summary>
    /// <param name="remainingBudget">The remaining tokens in the current execution context.</param>
    /// <param name="requestedTokens">The estimated tokens required by the rejected request.</param>
    public TokenBudgetExceededException(int remainingBudget, int requestedTokens)
        : base($"Token budget exceeded: {requestedTokens:N0} tokens requested but only {remainingBudget:N0} remaining.")
    {
        RemainingBudget = remainingBudget;
        RequestedTokens = requestedTokens;
    }
}

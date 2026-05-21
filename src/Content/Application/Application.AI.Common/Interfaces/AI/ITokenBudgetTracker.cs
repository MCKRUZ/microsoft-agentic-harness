namespace Application.AI.Common.Interfaces.AI;

/// <summary>
/// Tracks token consumption across agent operations within an execution context.
/// Implementations enforce per-request budget limits to prevent runaway LLM costs.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="Application.AI.Common.Interfaces.IBudgetTrackingService"/>, which
/// tracks cumulative USD spend across periods and agents, <see cref="ITokenBudgetTracker"/>
/// operates at the individual execution-context level — enforcing a hard token ceiling
/// for a single agent turn or plan step.
/// </para>
/// <para>
/// The <see cref="Application.AI.Common.MediatRBehaviors.TokenBudgetBehavior{TRequest,TResponse}"/>
/// consults this tracker before forwarding any request that implements
/// <see cref="Application.AI.Common.Interfaces.MediatR.IConsumesTokens"/>.
/// </para>
/// </remarks>
public interface ITokenBudgetTracker
{
    /// <summary>Gets the remaining token budget for the current execution context.</summary>
    int RemainingBudget { get; }

    /// <summary>Gets the total token budget configured for the execution context.</summary>
    int TotalBudget { get; }

    /// <summary>Records token consumption from a completed LLM operation.</summary>
    /// <param name="tokensUsed">The number of tokens consumed by the operation. Must be non-negative.</param>
    void RecordUsage(int tokensUsed);

    /// <summary>Returns <see langword="true"/> if the remaining budget can accommodate the estimated token cost.</summary>
    /// <param name="estimatedTokens">The projected token cost of the pending operation.</param>
    bool CanAfford(int estimatedTokens);
}

using Application.AI.Common.Exceptions;
using Domain.AI.Context;

namespace Application.AI.Common.Interfaces.Context;

/// <summary>
/// Tracks token allocations per agent across context components (system prompt, tools, skills, history).
/// Thread-safe for concurrent agent execution.
/// </summary>
/// <remarks>
/// <para>
/// Each agent has an independent budget tracked by name. Components are named categories
/// like "system_prompt", "tool_schemas", "skill_context", "conversation_history".
/// </para>
/// <para>
/// The tracker does not enforce a global budget — each agent's total budget is passed
/// per-call to <see cref="GetRemainingBudget"/> and <see cref="EnsureBudget"/>.
/// This allows different agents to have different context window sizes.
/// </para>
/// </remarks>
public interface IContextBudgetTracker
{
    /// <summary>
    /// Records a token allocation for a named component within an agent's budget.
    /// </summary>
    /// <param name="agentName">The agent identifier.</param>
    /// <param name="component">The component name (e.g., "system_prompt", "tool_schemas").</param>
    /// <param name="tokens">The number of tokens to allocate.</param>
    void RecordAllocation(string agentName, string component, int tokens);

    /// <summary>
    /// Gets the total tokens allocated for an agent across all components.
    /// </summary>
    /// <param name="agentName">The agent identifier.</param>
    /// <returns>Total allocated tokens, or 0 if the agent is not tracked.</returns>
    int GetTotalAllocated(string agentName);

    /// <summary>
    /// Gets the remaining budget for an agent given its total budget.
    /// </summary>
    /// <param name="agentName">The agent identifier.</param>
    /// <param name="totalBudget">The agent's total token budget.</param>
    /// <returns>Remaining tokens, clamped to 0 (never negative).</returns>
    int GetRemainingBudget(string agentName, int totalBudget);

    /// <summary>
    /// Checks if adding tokens would exceed budget.
    /// Throws <see cref="ContextBudgetExceededException"/> if the allocation would exceed the budget.
    /// </summary>
    /// <param name="agentName">The agent identifier.</param>
    /// <param name="additionalTokens">The tokens to be added.</param>
    /// <param name="totalBudget">The agent's total token budget.</param>
    /// <exception cref="ContextBudgetExceededException">
    /// Thrown when <c>GetTotalAllocated(agentName) + additionalTokens</c> exceeds <paramref name="totalBudget"/>.
    /// </exception>
    void EnsureBudget(string agentName, int additionalTokens, int totalBudget);

    /// <summary>
    /// Resets all tracking for an agent (e.g., after context compaction).
    /// </summary>
    /// <param name="agentName">The agent identifier to reset.</param>
    void Reset(string agentName);

    /// <summary>
    /// Gets per-component token breakdown for an agent.
    /// </summary>
    /// <param name="agentName">The agent identifier.</param>
    /// <returns>A read-only snapshot of component allocations. Empty if agent is not tracked.</returns>
    IReadOnlyDictionary<string, int> GetBreakdown(string agentName);

    /// <summary>
    /// Assesses whether the agent should continue producing output based on
    /// budget consumption and diminishing returns detection.
    /// </summary>
    /// <param name="agentName">The agent identifier.</param>
    /// <param name="totalBudget">The agent's total token budget.</param>
    /// <returns>A budget assessment with the recommended action.</returns>
    BudgetAssessment AssessContinuation(string agentName, int totalBudget);

    /// <summary>
    /// Records a continuation turn with the number of tokens produced.
    /// Used by the diminishing returns detector to track output velocity.
    /// </summary>
    /// <param name="agentName">The agent identifier.</param>
    /// <param name="tokensProduced">Tokens produced in this continuation turn.</param>
    void RecordContinuation(string agentName, int tokensProduced);
}

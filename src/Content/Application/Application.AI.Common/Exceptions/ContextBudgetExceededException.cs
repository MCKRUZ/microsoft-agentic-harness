using Application.Common.Exceptions;

namespace Application.AI.Common.Exceptions;

/// <summary>
/// Represents an exception thrown when an agent's context window or token budget has been
/// exhausted. This exception provides structured context about the limits, actual usage,
/// and which agent was affected.
/// </summary>
/// <remarks>
/// Context budget management is critical in agentic systems where LLM context windows have
/// hard limits. When an agent exceeds its budget, the orchestration loop must decide whether
/// to compact the conversation, spawn a subagent with fresh context, or terminate gracefully.
/// Common scenarios include:
/// <list type="bullet">
///   <item><description>Conversation history exceeded the model's context window</description></item>
///   <item><description>Tool results pushed the total token count past the configured limit</description></item>
///   <item><description>Skill injection consumed more context than budgeted</description></item>
///   <item><description>A subagent accumulated too much state during a long-running task</description></item>
///   <item><description>System prompt plus tools exhausted available context before user input</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// if (tokensUsed > tokenLimit)
/// {
///     throw new ContextBudgetExceededException(tokenLimit, tokensUsed, agentName);
/// }
/// </code>
/// </example>
public sealed class ContextBudgetExceededException : ApplicationExceptionBase
{
    /// <summary>
    /// Gets the configured token limit that was exceeded.
    /// </summary>
    /// <value>The maximum number of tokens allowed for this context.</value>
    public long TokenLimit { get; }

    /// <summary>
    /// Gets the actual number of tokens used when the budget was exceeded.
    /// </summary>
    /// <value>The token count at the time of the violation.</value>
    public long TokensUsed { get; }

    /// <summary>
    /// Gets the name or identifier of the agent that exceeded its budget, if specified.
    /// </summary>
    /// <value>The agent name (e.g., "planner", "code-reviewer"), or <c>null</c> if not provided.</value>
    public string? AgentName { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextBudgetExceededException"/> class
    /// with a default error message.
    /// </summary>
    public ContextBudgetExceededException()
        : base("The agent's context budget has been exceeded.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextBudgetExceededException"/> class
    /// with a custom error message.
    /// </summary>
    /// <param name="message">A message describing the context budget violation.</param>
    public ContextBudgetExceededException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextBudgetExceededException"/> class
    /// with a custom error message and a reference to the inner exception that caused it.
    /// </summary>
    /// <param name="message">A message describing the context budget violation.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public ContextBudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextBudgetExceededException"/> class
    /// with structured context about the budget violation.
    /// </summary>
    /// <param name="tokenLimit">The configured maximum token count. Must be non-negative.</param>
    /// <param name="tokensUsed">The actual token count when the limit was exceeded. Must be non-negative.</param>
    /// <param name="agentName">
    /// The name of the agent that exceeded its budget.
    /// Pass <c>null</c> if the violation occurred outside of a named agent context.
    /// </param>
    /// <example>
    /// <code>
    /// throw new ContextBudgetExceededException(128000, 131072, "planner");
    /// // Message: "Agent 'planner' exceeded context budget: 131,072 tokens used of 128,000 allowed."
    ///
    /// throw new ContextBudgetExceededException(200000, 205000, null);
    /// // Message: "Context budget exceeded: 205,000 tokens used of 200,000 allowed."
    /// </code>
    /// </example>
    public ContextBudgetExceededException(long tokenLimit, long tokensUsed, string? agentName = null)
        : base(
            agentName is not null
                ? $"Agent '{agentName}' exceeded context budget: {tokensUsed:N0} tokens used of {tokenLimit:N0} allowed."
                : $"Context budget exceeded: {tokensUsed:N0} tokens used of {tokenLimit:N0} allowed.")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tokenLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(tokensUsed);
        TokenLimit = tokenLimit;
        TokensUsed = tokensUsed;
        AgentName = agentName;
    }
}

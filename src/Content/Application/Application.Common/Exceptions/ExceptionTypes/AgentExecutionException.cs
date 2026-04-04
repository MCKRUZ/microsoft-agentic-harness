using Application.Common.Exceptions;

namespace Application.Common.Exceptions.ExceptionTypes;

/// <summary>
/// Represents an exception thrown when the agent orchestration loop encounters an
/// unrecoverable error. This exception wraps failures that occur during the agent's
/// conversation loop, tool dispatch, or state management.
/// </summary>
/// <remarks>
/// This exception captures agent-level failures that are distinct from individual tool failures
/// (see <see cref="ToolExecutionException"/>). It represents systemic issues with the agent's
/// ability to continue processing. Common scenarios include:
/// <list type="bullet">
///   <item><description>The agent exceeded its maximum turn or iteration limit</description></item>
///   <item><description>An infinite tool-call loop was detected</description></item>
///   <item><description>The underlying LLM API returned an unrecoverable error</description></item>
///   <item><description>Content safety middleware blocked the agent's response</description></item>
///   <item><description>Agent state became corrupted or inconsistent</description></item>
///   <item><description>A required middleware in the agent pipeline failed</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// if (turnCount > maxTurns)
/// {
///     throw new AgentExecutionException(agentName, $"Exceeded maximum turn limit of {maxTurns}.");
/// }
/// </code>
/// </example>
public sealed class AgentExecutionException : ApplicationExceptionBase
{
    /// <summary>
    /// Gets the name or identifier of the agent that failed, if specified.
    /// </summary>
    /// <value>The agent name (e.g., "planner", "code-reviewer"), or <c>null</c> if not provided.</value>
    public string? AgentName { get; }

    /// <summary>
    /// Gets a structured reason for the failure, if specified.
    /// </summary>
    /// <value>A description of why the agent execution failed, or <c>null</c> if not provided.</value>
    public string? Reason { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentExecutionException"/> class
    /// with a default error message.
    /// </summary>
    public AgentExecutionException()
        : base("The agent encountered an unrecoverable error during execution.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentExecutionException"/> class
    /// with a custom error message.
    /// </summary>
    /// <param name="message">A message describing the agent execution failure.</param>
    public AgentExecutionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentExecutionException"/> class
    /// with a custom error message and a reference to the inner exception that caused it.
    /// </summary>
    /// <param name="message">A message describing the agent execution failure.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public AgentExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentExecutionException"/> class
    /// with structured context about the failed agent execution.
    /// </summary>
    /// <param name="agentName">The name or identifier of the agent that failed.</param>
    /// <param name="reason">A description of why the agent failed. Pass <c>null</c> if the reason is unknown.</param>
    /// <param name="innerException">The optional underlying exception that caused the failure.</param>
    /// <example>
    /// <code>
    /// throw new AgentExecutionException("planner", "Exceeded maximum turn limit of 50.");
    /// // Message: "Agent 'planner' failed: Exceeded maximum turn limit of 50."
    ///
    /// throw new AgentExecutionException("code-reviewer", null);
    /// // Message: "Agent 'code-reviewer' encountered an unrecoverable error."
    /// </code>
    /// </example>
    public AgentExecutionException(string agentName, string? reason, Exception? innerException = null)
        : base(
            reason is not null
                ? $"Agent '{agentName}' failed: {reason}"
                : $"Agent '{agentName}' encountered an unrecoverable error.",
            innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        AgentName = agentName;
        Reason = reason;
    }
}

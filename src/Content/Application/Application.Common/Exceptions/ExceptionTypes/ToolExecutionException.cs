using Application.Common.Exceptions;

namespace Application.Common.Exceptions.ExceptionTypes;

/// <summary>
/// Represents an exception thrown when a tool invocation fails during agent execution.
/// This exception provides structured context about which tool failed and why, enabling
/// the agent orchestration loop to make informed recovery decisions.
/// </summary>
/// <remarks>
/// Tools are the primary mechanism by which agents interact with external systems. When a tool
/// call fails, the agent needs structured error information to decide whether to retry, fall back
/// to an alternative tool, or report the failure to the user. Common scenarios include:
/// <list type="bullet">
///   <item><description>Tool input validation failures at the execution boundary</description></item>
///   <item><description>External service timeouts or connectivity issues during tool execution</description></item>
///   <item><description>Permission denials when a tool attempts a restricted operation</description></item>
///   <item><description>Unexpected runtime errors within tool implementation logic</description></item>
///   <item><description>Tool result size exceeding configured limits</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// catch (HttpRequestException ex)
/// {
///     throw new ToolExecutionException("web_fetch", "Target URL returned HTTP 503.", ex);
/// }
/// </code>
/// </example>
public sealed class ToolExecutionException : ApplicationExceptionBase
{
    /// <summary>
    /// Gets the name or key of the tool that failed, if specified.
    /// </summary>
    /// <value>The tool identifier (e.g., "file_system", "web_fetch"), or <c>null</c> if not provided.</value>
    public string? ToolName { get; }

    /// <summary>
    /// Gets a structured reason for the failure, if specified.
    /// </summary>
    /// <value>A description of why the tool failed, or <c>null</c> if not provided.</value>
    public string? Reason { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolExecutionException"/> class
    /// with a default error message.
    /// </summary>
    public ToolExecutionException()
        : base("A tool execution failed to complete successfully.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolExecutionException"/> class
    /// with a custom error message.
    /// </summary>
    /// <param name="message">A message describing the tool execution failure.</param>
    public ToolExecutionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolExecutionException"/> class
    /// with a custom error message and a reference to the inner exception that caused it.
    /// </summary>
    /// <param name="message">A message describing the tool execution failure.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public ToolExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolExecutionException"/> class
    /// with structured context about the failed tool invocation.
    /// </summary>
    /// <param name="toolName">The name or key of the tool that failed (e.g., "file_system", "calculation_engine").</param>
    /// <param name="reason">A description of why the tool failed. Pass <c>null</c> if the reason is unknown.</param>
    /// <param name="innerException">The optional underlying exception that caused the failure.</param>
    /// <example>
    /// <code>
    /// throw new ToolExecutionException("calculation_engine", "Division by zero in expression.");
    /// // Message: "Tool 'calculation_engine' failed: Division by zero in expression."
    ///
    /// throw new ToolExecutionException("file_system", null);
    /// // Message: "Tool 'file_system' failed to execute."
    /// </code>
    /// </example>
    public ToolExecutionException(string toolName, string? reason, Exception? innerException = null)
        : base(
            reason is not null
                ? $"Tool '{toolName}' failed: {reason}"
                : $"Tool '{toolName}' failed to execute.",
            innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ToolName = toolName;
        Reason = reason;
    }
}

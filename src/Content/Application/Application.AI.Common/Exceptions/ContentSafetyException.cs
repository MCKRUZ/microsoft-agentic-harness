using Application.Common.Exceptions;

namespace Application.AI.Common.Exceptions;

/// <summary>
/// Represents an exception thrown when content is blocked by safety middleware during
/// agent execution. This exception provides structured context about the reason and
/// category of the safety violation.
/// </summary>
/// <remarks>
/// Content safety is a critical concern in agentic systems where LLM-generated output
/// and user-provided input are processed. Microsoft Agent Framework integrates content
/// safety middleware that can block requests or responses. This exception enables the
/// orchestration loop to handle safety blocks gracefully. Common scenarios include:
/// <list type="bullet">
///   <item><description>Agent-generated content flagged for harmful categories</description></item>
///   <item><description>User input blocked before reaching the agent</description></item>
///   <item><description>Tool output containing content that violates safety policies</description></item>
///   <item><description>PII (Personally Identifiable Information) detected in responses</description></item>
///   <item><description>Jailbreak or prompt injection attempts detected</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// if (safetyResult.IsBlocked)
/// {
///     throw new ContentSafetyException(
///         safetyResult.BlockReason,
///         safetyResult.Category);
/// }
/// </code>
/// </example>
public sealed class ContentSafetyException : ApplicationExceptionBase
{
    /// <summary>
    /// Gets the category of the safety violation, if specified.
    /// </summary>
    /// <value>
    /// The safety category (e.g., "hate", "violence", "self-harm", "sexual", "pii", "jailbreak"),
    /// or <c>null</c> if the category is unknown or not classified.
    /// </value>
    public string? Category { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentSafetyException"/> class
    /// with a default error message.
    /// </summary>
    public ContentSafetyException()
        : base("Content was blocked by safety middleware.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentSafetyException"/> class
    /// with a custom error message.
    /// </summary>
    /// <param name="message">A message describing why the content was blocked.</param>
    public ContentSafetyException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentSafetyException"/> class
    /// with a custom error message and a reference to the inner exception that caused it.
    /// </summary>
    /// <param name="message">A message describing why the content was blocked.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public ContentSafetyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentSafetyException"/> class
    /// with structured context about the safety violation.
    /// </summary>
    /// <param name="reason">A description of why the content was blocked.</param>
    /// <param name="category">
    /// The safety category that was violated (e.g., "hate", "violence", "self-harm", "sexual", "pii").
    /// Pass <c>null</c> if the category is unknown.
    /// </param>
    /// <example>
    /// <code>
    /// throw new ContentSafetyException("Response contained harmful content.", "violence");
    /// // Message: "Content blocked [violence]: Response contained harmful content."
    ///
    /// throw new ContentSafetyException("PII detected in agent output.", "pii");
    /// // Message: "Content blocked [pii]: PII detected in agent output."
    /// </code>
    /// </example>
    public ContentSafetyException(string reason, string? category)
        : base(FormatMessage(reason, category))
    {
        Category = category;
    }

    private static string FormatMessage(string reason, string? category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return category is not null
            ? $"Content blocked [{category}]: {reason}"
            : $"Content blocked: {reason}";
    }
}

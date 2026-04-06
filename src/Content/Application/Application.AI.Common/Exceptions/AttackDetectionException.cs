using Application.Common.Exceptions;
using System.Collections;

namespace Application.AI.Common.Exceptions;

/// <summary>
/// Exception thrown when prompt injection, jailbreak, or other adversarial attacks
/// are detected in user prompts or documents. Contains structured analysis data
/// about the detected threats.
/// </summary>
/// <remarks>
/// This exception is raised by attack detection middleware (e.g., Microsoft Prompt Shield)
/// when adversarial content is identified. The analysis data enables logging, auditing,
/// and informed decision-making about how to handle the detected attack. Common scenarios include:
/// <list type="bullet">
///   <item><description>Direct prompt injection attempts in user input</description></item>
///   <item><description>Indirect prompt injection embedded in documents or tool outputs</description></item>
///   <item><description>Jailbreak attempts to bypass system instructions</description></item>
///   <item><description>Multi-turn manipulation patterns detected across conversation history</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// if (shieldResult.IsAttackDetected)
/// {
///     throw new AttackDetectionException("Prompt injection detected in user input.")
///     {
///         UserPromptAttackDetected = true,
///         DetectedCategories = shieldResult.Categories
///     };
/// }
/// </code>
/// </example>
public sealed class AttackDetectionException : ApplicationExceptionBase
{
    /// <summary>
    /// Gets whether an attack was detected in the user's prompt.
    /// </summary>
    public bool UserPromptAttackDetected { get; init; }

    /// <summary>
    /// Gets the number of documents in which attacks were detected.
    /// </summary>
    /// <value>Zero if no documents contained attacks, or the count of affected documents.</value>
    public int DocumentsWithAttacksCount { get; init; }

    /// <summary>
    /// Gets the categories of attacks that were detected, if available.
    /// </summary>
    /// <value>
    /// A list of attack category identifiers (e.g., "UserPrompt", "DocumentAttack"),
    /// or an empty list if categories are not available.
    /// </value>
    public IReadOnlyList<string> DetectedCategories { get; init; } = [];

    /// <summary>
    /// Gets a dictionary containing the analysis data for exception handling and logging.
    /// Provides structured access to the attack detection results.
    /// </summary>
    private IDictionary? _data;

    /// <inheritdoc />
    public override IDictionary Data => _data ??= new Dictionary<string, object?>
    {
        ["userPromptAttackDetected"] = UserPromptAttackDetected,
        ["documentsWithAttacksCount"] = DocumentsWithAttacksCount,
        ["detectedCategories"] = DetectedCategories
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="AttackDetectionException"/> class.
    /// </summary>
    public AttackDetectionException()
        : base("An adversarial attack was detected in the input.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AttackDetectionException"/> class
    /// with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the detected attack.</param>
    public AttackDetectionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AttackDetectionException"/> class
    /// with a specified error message and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the detected attack.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public AttackDetectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

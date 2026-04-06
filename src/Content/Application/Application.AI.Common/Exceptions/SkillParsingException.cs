using Application.Common.Exceptions;

namespace Application.AI.Common.Exceptions;

/// <summary>
/// Represents an exception thrown when parsing of a SKILL.md file or skill declaration fails.
/// This exception wraps the underlying parsing error to provide context about which skill
/// file was malformed.
/// </summary>
/// <remarks>
/// This exception is raised when the agentic harness encounters malformed or invalid
/// skill declarations in SKILL.md files. Common causes include:
/// <list type="bullet">
///   <item><description>Invalid YAML syntax in the skill declaration</description></item>
///   <item><description>Missing required fields (name, description, type, operations)</description></item>
///   <item><description>Invalid operation definitions or parameter schemas</description></item>
///   <item><description>Schema validation failures against the skill specification</description></item>
///   <item><description>File encoding issues preventing proper parsing</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// catch (YamlException ex)
/// {
///     throw new SkillParsingException(
///         $"Failed to parse skill definition at '{filePath}'.", ex);
/// }
/// </code>
/// </example>
public sealed class SkillParsingException : ApplicationExceptionBase
{
    /// <summary>
    /// Gets the file path of the skill definition that failed to parse, if specified.
    /// </summary>
    /// <value>The path to the SKILL.md or skill declaration file, or <c>null</c> if not provided.</value>
    public string? FilePath { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillParsingException"/> class
    /// with a specified error message and a reference to the inner exception that caused it.
    /// </summary>
    /// <param name="message">The message that describes the parsing error.</param>
    /// <param name="innerException">The exception that caused the parsing failure.</param>
    public SkillParsingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillParsingException"/> class
    /// with structured context about the failed skill file.
    /// </summary>
    /// <param name="filePath">The path to the skill file that failed to parse.</param>
    /// <param name="reason">A description of the parsing failure.</param>
    /// <param name="innerException">The optional underlying exception that caused the failure.</param>
    /// <example>
    /// <code>
    /// throw new SkillParsingException("skills/code-review/SKILL.md", "Missing required 'name' field.");
    /// // Message: "Failed to parse skill at 'skills/code-review/SKILL.md': Missing required 'name' field."
    /// </code>
    /// </example>
    public SkillParsingException(string filePath, string reason, Exception? innerException = null)
        : base($"Failed to parse skill at '{filePath}': {reason}", innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        FilePath = filePath;
    }
}

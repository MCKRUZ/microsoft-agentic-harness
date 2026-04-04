using Application.Common.Exceptions;

namespace Application.Common.Exceptions.ExceptionTypes;

/// <summary>
/// Represents an exception thrown when a requested skill cannot be found or loaded.
/// This exception provides structured context about which skill was requested and
/// from which source it was expected to be available.
/// </summary>
/// <remarks>
/// Skills are first-class components in the agentic harness, loaded from multiple sources
/// (bundled, filesystem, MCP servers). This exception is semantically distinct from
/// <see cref="EntityNotFoundException"/> because skills have source-awareness and their
/// absence may indicate a configuration issue rather than a data issue. Common scenarios include:
/// <list type="bullet">
///   <item><description>A skill referenced in an agent manifest (AGENT.md) does not exist</description></item>
///   <item><description>A filesystem skill directory is missing or inaccessible</description></item>
///   <item><description>An MCP server failed to expose an expected skill</description></item>
///   <item><description>A bundled skill was disabled by configuration</description></item>
///   <item><description>A skill name was misspelled in a tool declaration</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// var skill = skillRegistry.Find(skillName)
///     ?? throw new SkillNotFoundException(skillName, "filesystem");
/// </code>
/// </example>
public sealed class SkillNotFoundException : ApplicationExceptionBase
{
    /// <summary>
    /// Gets the name of the skill that was not found, if specified.
    /// </summary>
    /// <value>The skill identifier (e.g., "code-review", "security-scan"), or <c>null</c> if not provided.</value>
    public string? SkillName { get; }

    /// <summary>
    /// Gets the source from which the skill was expected to be loaded, if specified.
    /// </summary>
    /// <value>
    /// The skill source (e.g., "bundled", "filesystem", "mcp", "plugin"),
    /// or <c>null</c> if the source is unknown or not relevant.
    /// </value>
    public string? SkillSource { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillNotFoundException"/> class
    /// with a default error message.
    /// </summary>
    public SkillNotFoundException()
        : base("The requested skill was not found.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillNotFoundException"/> class
    /// with a custom error message.
    /// </summary>
    /// <param name="message">A message describing why the skill was not found.</param>
    public SkillNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillNotFoundException"/> class
    /// with a custom error message and a reference to the inner exception that caused it.
    /// </summary>
    /// <param name="message">A message describing why the skill was not found.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public SkillNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillNotFoundException"/> class
    /// with structured context about the missing skill.
    /// </summary>
    /// <param name="skillName">The name of the skill that was not found.</param>
    /// <param name="source">
    /// The source from which the skill was expected (e.g., "bundled", "filesystem", "mcp").
    /// Pass <c>null</c> if the source is unknown.
    /// </param>
    /// <example>
    /// <code>
    /// throw new SkillNotFoundException("code-review", "filesystem");
    /// // Message: "Skill 'code-review' was not found in source 'filesystem'."
    ///
    /// throw new SkillNotFoundException("security-scan", null);
    /// // Message: "Skill 'security-scan' was not found."
    /// </code>
    /// </example>
    public SkillNotFoundException(string skillName, string? source)
        : base(
            source is not null
                ? $"Skill '{skillName}' was not found in source '{source}'."
                : $"Skill '{skillName}' was not found.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        SkillName = skillName;
        SkillSource = source;
    }
}

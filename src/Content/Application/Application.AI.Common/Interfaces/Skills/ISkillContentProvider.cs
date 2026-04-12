namespace Application.AI.Common.Interfaces.Skills;

/// <summary>
/// Provides skill file content by path. Abstraction over filesystem and in-memory sources.
/// Implementors return null when the requested path is not available from their source,
/// allowing callers to fall back to alternative providers.
/// </summary>
public interface ISkillContentProvider
{
    /// <summary>Key used to store the provider in <c>AgentExecutionContext.AdditionalProperties</c>.</summary>
    public const string AdditionalPropertiesKey = "__skillContentProvider";

    /// <summary>
    /// Returns the content of the skill file at <paramref name="skillPath"/>,
    /// or null if this provider does not have content for that path.
    /// </summary>
    Task<string?> GetSkillContentAsync(string skillPath, CancellationToken cancellationToken = default);
}

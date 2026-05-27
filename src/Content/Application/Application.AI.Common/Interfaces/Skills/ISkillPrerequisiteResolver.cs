using Application.AI.Common.Models;
using Domain.AI.Skills;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Interfaces.Skills;

/// <summary>
/// Builds the prerequisite metadata map from resolved skills and their tools.
/// Maps each skill to its prerequisites, completion tool, and owned tool names
/// for consumption by <see cref="Middleware.SkillPrerequisiteMiddleware"/>.
/// </summary>
public interface ISkillPrerequisiteResolver
{
    /// <summary>
    /// Builds a prerequisite map from the given skills and their resolved tools.
    /// </summary>
    /// <param name="skills">The skill definitions to process.</param>
    /// <param name="resolvedTools">The already-resolved tools to match against skill declarations.</param>
    /// <returns>A prerequisite map. Check <see cref="SkillPrerequisiteMap.HasAnyPrerequisites"/> to determine if any prerequisites exist.</returns>
    SkillPrerequisiteMap BuildPrerequisiteMap(
        IReadOnlyList<SkillDefinition> skills,
        IReadOnlyList<AITool> resolvedTools);
}

using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Models;
using Domain.AI.Skills;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Services.Skills;

/// <summary>
/// Builds prerequisite metadata maps from resolved skills and their tools. Maps each skill
/// to its prerequisites, completion tool, and owned tool names by cross-referencing skill
/// declarations against the resolved tool list.
/// </summary>
public class SkillPrerequisiteResolver : ISkillPrerequisiteResolver
{
    /// <inheritdoc />
    public SkillPrerequisiteMap BuildPrerequisiteMap(
        IReadOnlyList<SkillDefinition> skills,
        IReadOnlyList<AITool> resolvedTools)
    {
        var entries = new Dictionary<string, SkillPrerequisiteEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in skills)
        {
            var declaredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (skill.AllowedTools?.Count > 0)
                foreach (var t in skill.AllowedTools) declaredNames.Add(t);
            if (skill.ToolDeclarations?.Count > 0)
                foreach (var td in skill.ToolDeclarations) declaredNames.Add(td.Name);
            if (skill.Tools?.Count > 0)
                foreach (var t in skill.Tools) declaredNames.Add(t.Name);

            var skillToolNames = resolvedTools
                .Where(t => declaredNames.Contains(t.Name))
                .Select(t => t.Name)
                .ToList();

            entries[skill.Id] = new SkillPrerequisiteEntry
            {
                SkillId = skill.Id,
                Prerequisites = skill.Prerequisites.ToList(),
                CompletionTool = skill.CompletionTool,
                ToolNames = skillToolNames
            };
        }

        return new SkillPrerequisiteMap { Skills = entries };
    }
}

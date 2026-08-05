using Application.AI.Common.Helpers;
using Domain.AI.Skills;
using Domain.Common.Config.AI;

namespace Application.AI.Common.Factories;

// Resolution half of AgentExecutionContextFactory: the per-skill decisions and naming that turn a
// skill plus its call options into the settings an agent context needs — deployment, framework type,
// tool ceiling, middleware chain, additional properties, and agent name.
//
// What these members have in common is not a shape but an absence: none of them touch the budget
// tracker, the trace store, or the context-provider rail. They read declarations and configuration
// and return a value, which is why they sit apart from the orchestration in the primary partial.
//
// Deliberately a plain comment, not an XML doc — see AgentExecutionContextFactory.ContextProviders.cs.
public partial class AgentExecutionContextFactory
{
    private static AIAgentFrameworkClientType? ResolveFrameworkTypeFromMetadata(SkillDefinition skill)
    {
        if (skill.Metadata?.TryGetValue("framework_type", out var value) == true
            && Enum.TryParse<AIAgentFrameworkClientType>(value?.ToString(), ignoreCase: true, out var parsed))
            return parsed;

        return null;
    }

    private string ResolveDeploymentName(SkillDefinition skill, SkillAgentOptions options)
    {
        if (!string.IsNullOrEmpty(options.DeploymentName))
            return options.DeploymentName;

        if (!string.IsNullOrEmpty(skill.ModelOverride))
            return skill.ModelOverride;

        if (skill.Metadata?.TryGetValue("deployment", out var deployment) == true)
            return deployment.ToString() ?? "default";

        return _appConfig.CurrentValue.AI?.AgentFramework?.DefaultDeployment ?? "default";
    }

    /// <summary>
    /// Resolves the single effective tool allowlist that governs an agent: the union of its skills'
    /// <c>AllowedTools</c> constraints, capped by the agent's declared ceiling (<paramref name="options"/>'s
    /// <see cref="SkillAgentOptions.AllowedTools"/>) and then by any explicit per-call
    /// <paramref name="explicitAllowlist"/>. Each cap can only tighten (see <see cref="ToolCeilingResolver"/>).
    /// Returns <see langword="null"/> when nothing restricts the agent (every tool is permitted); a
    /// non-null list is an active restriction, and an empty one denies every tool.
    /// </summary>
    private static IReadOnlyList<string>? ResolveEffectiveAllowlist(
        IReadOnlyList<SkillDefinition> skills,
        SkillAgentOptions options,
        IReadOnlyList<string>? explicitAllowlist)
    {
        var effective = ToolCeilingResolver.ApplyCeiling(MergeSkillAllowedTools(skills), options.AllowedTools);
        return ToolCeilingResolver.ApplyCeiling(effective, explicitAllowlist);
    }

    /// <summary>
    /// Deduplicated union of every skill's <c>AllowedTools</c> constraint, case-insensitively, or
    /// <see langword="null"/> when no skill declares a constraint — the "unbounded" input the ceiling
    /// resolver expects for "no restriction" (distinct from an empty list, which means deny all).
    /// </summary>
    private static IReadOnlyList<string>? MergeSkillAllowedTools(IReadOnlyList<SkillDefinition> skills)
    {
        var union = skills
            .Where(s => s.AllowedTools?.Count > 0)
            .SelectMany(s => s.AllowedTools!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return union.Count > 0 ? union : null;
    }

    private List<Type>? ResolveMiddlewareTypes(SkillDefinition skill, SkillAgentOptions options)
    {
        var types = new List<Type>();

        types.Add(typeof(Middleware.ObservabilityMiddleware));
        types.Add(typeof(Middleware.ToolDiagnosticsMiddleware));

        if (options.MiddlewareTypes?.Count > 0)
            types.AddRange(options.MiddlewareTypes);

        return types.Count > 0 ? types : null;
    }

    private static Dictionary<string, object> BuildAdditionalProperties(SkillDefinition skill, SkillAgentOptions options)
    {
        var props = new Dictionary<string, object>
        {
            ["skillId"] = skill.Id,
            ["skillName"] = skill.Name,
            ["loadedAt"] = skill.LoadedAt.ToString("O")
        };

        if (!string.IsNullOrEmpty(skill.Category))
            props["category"] = skill.Category;
        if (skill.HasTags)
            props["tags"] = skill.Tags;
        if (!string.IsNullOrEmpty(skill.Version))
            props["version"] = skill.Version;

        if (skill.Metadata != null)
        {
            foreach (var (key, value) in skill.Metadata)
                props[$"skill_{key}"] = value;
        }

        if (options.AdditionalProperties != null)
        {
            foreach (var (key, value) in options.AdditionalProperties)
                props[key] = value;
        }

        return props;
    }

    private static string ToAgentName(string skillName)
    {
        var parts = skillName.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        var pascal = string.Concat(parts.Select(p =>
            char.ToUpperInvariant(p[0]) + p[1..]));
        return pascal.EndsWith("Agent", StringComparison.OrdinalIgnoreCase)
            ? pascal
            : pascal + "Agent";
    }
}

using System.Text;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Prompts;
using Domain.AI.Permissions;
using Domain.AI.Prompts;

namespace Infrastructure.AI.Prompts.Sections;

/// <summary>
/// Provides the permission rules section — formats active permission rules as
/// natural language constraints so the agent understands approval requirements.
/// Cacheable because permission rules are typically static within a session.
/// </summary>
/// <remarks>
/// One line per tool, under the name the agent invokes it by: a rule's pattern is that name, since a
/// first-party tool's registration key and published name are the same (#888). Identical lines are
/// merged, which covers two providers restricting the same tool.
/// </remarks>
public sealed class PermissionRulesSectionProvider : IPromptSectionProvider
{
    private readonly IEnumerable<IPermissionRuleProvider> _ruleProviders;

    /// <summary>
    /// Initializes a new instance of <see cref="PermissionRulesSectionProvider"/>.
    /// </summary>
    /// <param name="ruleProviders">All registered permission rule providers.</param>
    public PermissionRulesSectionProvider(IEnumerable<IPermissionRuleProvider> ruleProviders)
    {
        ArgumentNullException.ThrowIfNull(ruleProviders);
        _ruleProviders = ruleProviders;
    }

    /// <inheritdoc />
    public SystemPromptSectionType SectionType => SystemPromptSectionType.PermissionRules;

    /// <inheritdoc />
    public async Task<SystemPromptSection?> GetSectionAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var allRules = new List<ToolPermissionRule>();

        foreach (var provider in _ruleProviders)
        {
            var rules = await provider.GetRulesAsync(agentId, cancellationToken);
            allRules.AddRange(rules);
        }

        if (allRules.Count == 0)
            return null;

        var content = FormatRules(allRules);

        return new SystemPromptSection(
            Name: "Permission Rules",
            Type: SystemPromptSectionType.PermissionRules,
            Priority: 40,
            IsCacheable: true,
            EstimatedTokens: TokenEstimationHelper.EstimateTokens(content),
            Content: content);
    }

    private static string FormatRules(List<ToolPermissionRule> rules)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Permission Rules");
        builder.AppendLine();

        var approvalRequired = SummaryLines(rules, PermissionBehaviorType.Ask);
        var denied = SummaryLines(rules, PermissionBehaviorType.Deny);

        if (approvalRequired.Count > 0)
        {
            builder.AppendLine("The following tools require approval before use:");
            foreach (var line in approvalRequired)
                builder.AppendLine($"- {line}");
            builder.AppendLine();
        }

        if (denied.Count > 0)
        {
            builder.AppendLine("The following tools are denied:");
            foreach (var line in denied)
                builder.AppendLine($"- {line}");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// The distinct summary lines for one behavior, one per logical tool-and-operation, in first-seen
    /// order. Grouping is on the tool's published name (see this type's remarks), so the two rules a
    /// provider emits for a key/published-name divergence collapse to the single name the agent can
    /// actually invoke.
    /// </summary>
    /// <remarks>
    /// Deliberately grouped <em>within</em> a behavior, never across: a tool legitimately appears under
    /// both headings when different rules restrict different operations on it, and collapsing those
    /// would drop a restriction from the summary rather than tidy it.
    /// </remarks>
    private static List<string> SummaryLines(List<ToolPermissionRule> rules, PermissionBehaviorType behavior)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            if (rule.Behavior != behavior)
                continue;

            var toolName = rule.ToolPattern;
            var operation = rule.OperationPattern is not null ? $" (operation: {rule.OperationPattern})" : "";
            var line = $"{toolName}{operation}";

            if (seen.Add(line))
                lines.Add(line);
        }

        return lines;
    }
}

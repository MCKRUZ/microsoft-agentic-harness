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

        var approvalRequired = rules
            .Where(r => r.Behavior == PermissionBehaviorType.Ask)
            .ToList();

        var denied = rules
            .Where(r => r.Behavior == PermissionBehaviorType.Deny)
            .ToList();

        if (approvalRequired.Count > 0)
        {
            builder.AppendLine("The following tools require approval before use:");
            foreach (var rule in approvalRequired)
            {
                var operation = rule.OperationPattern is not null ? $" (operation: {rule.OperationPattern})" : "";
                builder.AppendLine($"- {rule.ToolPattern}{operation}");
            }
            builder.AppendLine();
        }

        if (denied.Count > 0)
        {
            builder.AppendLine("The following tools are denied:");
            foreach (var rule in denied)
            {
                var operation = rule.OperationPattern is not null ? $" (operation: {rule.OperationPattern})" : "";
                builder.AppendLine($"- {rule.ToolPattern}{operation}");
            }
        }

        return builder.ToString().TrimEnd();
    }
}

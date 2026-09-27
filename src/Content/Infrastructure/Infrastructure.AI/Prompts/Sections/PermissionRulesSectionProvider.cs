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
/// <para>
/// <strong>One line per logical tool, under the name the agent actually invokes (#652).</strong> The
/// rule providers deliberately emit <em>two</em> rules for a first-party tool whose DI registration
/// key disagrees with its self-reported published name — one per form — so enforcement covers the
/// tool whichever name a caller uses (#612, #626). That is right for enforcement and wrong for this
/// summary: rendered verbatim it told the model that two distinct tools were restricted, one of them
/// under a registration key the model can never call, since
/// <c>ThreePhasePermissionResolver.Matches</c> matches on the published name at invocation.
/// </para>
/// <para>
/// Grouping therefore reads <see cref="ToolPermissionRule.PublishedToolName"/>, which the emitting
/// provider stamps on both rules of such a pair. <strong>This provider deliberately does NOT resolve
/// tool names itself.</strong> Doing so was tried and reverted: <c>PluginPermissionRuleProvider</c>'s
/// unverified-boundary fallback emits one rule per <em>every</em> registered first-party tool, so
/// resolving each rule's pattern here would construct the host's entire tool set as a side effect of
/// composing a system prompt — the precise trade-off that method's own remarks record as deliberately
/// refused, and the shape that broke host boot at #524. A name-form pairing is only knowable at
/// emission anyway; recovering it later necessarily means resolving the tool again.
/// </para>
/// <para>
/// Identical lines are also merged, which costs nothing and covers duplication the pairing tag cannot
/// see — two independent providers restricting the same tool under the same name.
/// </para>
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

            // The published name when this rule is one form of a name-pair, else the pattern itself —
            // which is the right answer for a glob (*, bash:*), an MCP tool, and the ordinary case
            // where a tool's key and published name already agree.
            var toolName = rule.PublishedToolName ?? rule.ToolPattern;
            var operation = rule.OperationPattern is not null ? $" (operation: {rule.OperationPattern})" : "";
            var line = $"{toolName}{operation}";

            if (seen.Add(line))
                lines.Add(line);
        }

        return lines;
    }
}

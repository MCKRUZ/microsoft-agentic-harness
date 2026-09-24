using System.Text;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Prompts;
using Application.AI.Common.Services.Tools;
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
/// So each rule's pattern is resolved back to its published name and the lines are grouped on that.
/// Resolving here rather than tagging the rules at creation is deliberate: it collapses <em>every</em>
/// source of duplication — including two independent providers restricting the same tool, which no
/// creation-time tag would catch — and keeps the alternate-name concern out of
/// <see cref="ToolPermissionRule"/>, which is shared by the whole permission system. It costs nothing
/// per call: <see cref="FirstPartyToolLookup"/> memoizes a published name for the process lifetime
/// (#651), and a name that is not a first-party key (a glob such as <c>*</c>, an MCP tool) resolves to
/// itself without ever probing the container.
/// </para>
/// </remarks>
public sealed class PermissionRulesSectionProvider : IPromptSectionProvider
{
    private readonly IEnumerable<IPermissionRuleProvider> _ruleProviders;
    private readonly FirstPartyToolLookup _firstPartyToolLookup;

    /// <summary>
    /// Initializes a new instance of <see cref="PermissionRulesSectionProvider"/>.
    /// </summary>
    /// <param name="ruleProviders">All registered permission rule providers.</param>
    /// <param name="firstPartyToolLookup">
    /// Resolves a rule's pattern to the tool's published name, so a tool covered under both its
    /// registration key and its published name is summarised once — see this type's remarks.
    /// </param>
    public PermissionRulesSectionProvider(
        IEnumerable<IPermissionRuleProvider> ruleProviders,
        FirstPartyToolLookup firstPartyToolLookup)
    {
        ArgumentNullException.ThrowIfNull(ruleProviders);
        ArgumentNullException.ThrowIfNull(firstPartyToolLookup);
        _ruleProviders = ruleProviders;
        _firstPartyToolLookup = firstPartyToolLookup;
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

    private string FormatRules(List<ToolPermissionRule> rules)
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
    private List<string> SummaryLines(List<ToolPermissionRule> rules, PermissionBehaviorType behavior)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            if (rule.Behavior != behavior)
                continue;

            var toolName = PublishedName(rule.ToolPattern);
            var operation = rule.OperationPattern is not null ? $" (operation: {rule.OperationPattern})" : "";
            var line = $"{toolName}{operation}";

            if (seen.Add(line))
                lines.Add(line);
        }

        return lines;
    }

    /// <summary>
    /// <paramref name="toolPattern"/>'s published name when it names a first-party tool whose
    /// registration key differs, and otherwise the pattern unchanged — which is the answer for a glob
    /// (<c>*</c>, <c>bash:*</c>), an MCP tool name, and the ordinary case where key and published name
    /// already agree.
    /// </summary>
    private string PublishedName(string toolPattern) =>
        _firstPartyToolLookup.TryResolvePublishedName(toolPattern, out var publishedName, out _)
            ? publishedName
            : toolPattern;
}

using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Permissions;
using Domain.AI.Agents;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.Permissions;

/// <summary>
/// Generates baseline <see cref="ToolPermissionRule"/> entries from an agent's autonomy tier.
/// Rules are derived from the <c>TierPolicies</c> configuration section, mapping each
/// <see cref="AutonomyLevel"/> to a default behavior and optional per-tool overrides.
/// </summary>
/// <remarks>
/// <para>
/// Tool overrides are emitted at Priority 10 as audit metadata only. In the current 3-phase
/// resolver design, phase ordering determines precedence (safety gates > session > baseline),
/// so per-tool specificity within the autonomy tier phase does not affect resolution outcome.
/// These overrides become enforceable when the resolver gains specificity-based cross-phase
/// precedence in a future iteration.
/// </para>
/// </remarks>
public sealed class AutonomyTierRuleProvider : IPermissionRuleProvider
{
    private readonly IAutonomyTierResolver _tierResolver;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<AutonomyTierRuleProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutonomyTierRuleProvider"/> class.
    /// </summary>
    /// <param name="tierResolver">Resolves the effective autonomy tier for an agent.</param>
    /// <param name="options">Application configuration containing tier policies.</param>
    /// <param name="logger">Logger for configuration warnings.</param>
    public AutonomyTierRuleProvider(
        IAutonomyTierResolver tierResolver,
        IOptionsMonitor<AppConfig> options,
        ILogger<AutonomyTierRuleProvider> logger)
    {
        _tierResolver = tierResolver;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public PermissionRuleSource Source => PermissionRuleSource.AutonomyTier;

    /// <inheritdoc />
    public Task<IReadOnlyList<ToolPermissionRule>> GetRulesAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var config = _options.CurrentValue.AI.Permissions;
        var tier = ResolveTier(agentId, config);
        var rules = BuildRules(tier, config);
        return Task.FromResult(rules);
    }

    private AutonomyLevel ResolveTier(string agentId, Domain.Common.Config.AI.Permissions.PermissionsConfig config)
    {
        if (Enum.TryParse<SubagentType>(agentId, ignoreCase: true, out var subagentType))
            return _tierResolver.Resolve(subagentType);

        if (Enum.TryParse<AutonomyLevel>(config.DefaultAutonomyLevel, ignoreCase: true, out var level))
            return level;

        _logger.LogWarning(
            "Invalid DefaultAutonomyLevel '{Level}' in PermissionsConfig, falling back to Supervised",
            config.DefaultAutonomyLevel);

        return AutonomyLevel.Supervised;
    }

    private IReadOnlyList<ToolPermissionRule> BuildRules(
        AutonomyLevel tier,
        Domain.Common.Config.AI.Permissions.PermissionsConfig config)
    {
        var tierName = tier.ToString();
        config.TierPolicies.TryGetValue(tierName, out var policy);

        var defaultBehaviorString = policy?.DefaultBehavior ?? config.DefaultBehavior;

        if (!Enum.TryParse<PermissionBehaviorType>(defaultBehaviorString, ignoreCase: true, out var defaultBehavior))
        {
            _logger.LogWarning(
                "Invalid default behavior '{Behavior}' for tier '{Tier}', falling back to Ask",
                defaultBehaviorString,
                tierName);
            defaultBehavior = PermissionBehaviorType.Ask;
        }

        var rules = new List<ToolPermissionRule>
        {
            new("*", null, defaultBehavior, PermissionRuleSource.AutonomyTier, Priority: 0)
        };

        if (policy?.ToolOverrides is not { Count: > 0 })
            return rules;

        foreach (var (toolName, behaviorString) in policy.ToolOverrides)
        {
            if (!Enum.TryParse<PermissionBehaviorType>(behaviorString, ignoreCase: true, out var overrideBehavior))
            {
                _logger.LogWarning(
                    "Invalid tool override behavior '{Behavior}' for tool '{Tool}' in tier '{Tier}', skipping",
                    behaviorString,
                    toolName,
                    tierName);
                continue;
            }

            rules.Add(new ToolPermissionRule(
                toolName,
                null,
                overrideBehavior,
                PermissionRuleSource.AutonomyTier,
                Priority: 10));
        }

        return rules;
    }
}

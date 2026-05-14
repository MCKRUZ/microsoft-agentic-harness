using Application.AI.Common.Interfaces.Agents;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Agents;
using Domain.AI.Governance;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Governance;

/// <summary>
/// Default implementation of <see cref="IAutonomyTierResolver"/> that resolves autonomy
/// levels from the <see cref="ISubagentProfileRegistry"/> with configuration-based fallback.
/// </summary>
public sealed class DefaultAutonomyTierResolver : IAutonomyTierResolver
{
    private readonly ISubagentProfileRegistry _registry;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<DefaultAutonomyTierResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultAutonomyTierResolver"/> class.
    /// </summary>
    /// <param name="registry">Registry of built-in subagent profiles.</param>
    /// <param name="options">Application configuration for default autonomy level fallback.</param>
    /// <param name="logger">Logger for resolution warnings.</param>
    public DefaultAutonomyTierResolver(
        ISubagentProfileRegistry registry,
        IOptionsMonitor<AppConfig> options,
        ILogger<DefaultAutonomyTierResolver> logger)
    {
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the autonomy level for the given subagent type by looking up its profile
    /// in the registry. Falls back to <c>PermissionsConfig.DefaultAutonomyLevel</c> if
    /// the type is not registered, then to <see cref="AutonomyLevel.Supervised"/> if the
    /// configuration value is invalid.
    /// </summary>
    /// <param name="agentType">The subagent type to resolve.</param>
    /// <returns>The effective autonomy level for the agent type.</returns>
    public AutonomyLevel Resolve(SubagentType agentType)
    {
        try
        {
            var definition = _registry.GetProfile(agentType);
            return definition.AutonomyLevel;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve profile for SubagentType '{AgentType}', falling back to config default",
                agentType);

            return ResolveFromConfig();
        }
    }

    /// <summary>
    /// Resolves the autonomy level directly from the provided subagent definition.
    /// </summary>
    /// <param name="definition">The subagent definition containing the autonomy level.</param>
    /// <returns>The autonomy level specified in the definition.</returns>
    public AutonomyLevel Resolve(SubagentDefinition definition)
    {
        return definition.AutonomyLevel;
    }

    private AutonomyLevel ResolveFromConfig()
    {
        var configValue = _options.CurrentValue.AI.Permissions.DefaultAutonomyLevel;

        if (Enum.TryParse<AutonomyLevel>(configValue, ignoreCase: true, out var level))
            return level;

        _logger.LogWarning(
            "Invalid DefaultAutonomyLevel '{Level}' in PermissionsConfig, falling back to Supervised",
            configValue);

        return AutonomyLevel.Supervised;
    }
}

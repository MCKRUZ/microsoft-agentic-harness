using Application.AI.Common.Interfaces.Agents;
using Domain.AI.Agents;
using Domain.AI.Permissions;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Agents;

/// <summary>
/// Provides predefined <see cref="SubagentDefinition"/> profiles for each
/// <see cref="SubagentType"/>. Profile defaults are combined with runtime
/// configuration from <c>AppConfig:AI:Orchestration:Subagent</c>.
/// </summary>
public sealed class BuiltInSubagentProfiles : ISubagentProfileRegistry
{
    private readonly IReadOnlyDictionary<SubagentType, SubagentDefinition> _profiles;

    /// <summary>
    /// Initializes the built-in profiles using configuration for dynamic defaults.
    /// </summary>
    /// <param name="options">Application configuration providing subagent turn limits.</param>
    public BuiltInSubagentProfiles(IOptionsMonitor<AppConfig> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var config = options.CurrentValue;
        var maxTurns = config.AI.Orchestration.Subagent.DefaultMaxTurnsPerSubagent;

        _profiles = new Dictionary<SubagentType, SubagentDefinition>
        {
            [SubagentType.Explore] = new()
            {
                AgentType = SubagentType.Explore,
                ToolAllowlist = ["file_system"],
                MaxTurns = 10,
                PermissionMode = PermissionBehaviorType.Allow,
                InheritParentTools = true
            },
            [SubagentType.Plan] = new()
            {
                AgentType = SubagentType.Plan,
                ToolAllowlist = [],
                MaxTurns = 3,
                PermissionMode = PermissionBehaviorType.Allow,
                InheritParentTools = false
            },
            [SubagentType.Verify] = new()
            {
                AgentType = SubagentType.Verify,
                ToolAllowlist = ["file_system"],
                MaxTurns = 5,
                PermissionMode = PermissionBehaviorType.Allow,
                InheritParentTools = true
            },
            [SubagentType.Execute] = new()
            {
                AgentType = SubagentType.Execute,
                ToolAllowlist = null,
                MaxTurns = maxTurns,
                PermissionMode = PermissionBehaviorType.Ask,
                InheritParentTools = true
            },
            [SubagentType.General] = new()
            {
                AgentType = SubagentType.General,
                ToolAllowlist = null,
                MaxTurns = 10,
                PermissionMode = PermissionBehaviorType.Ask,
                InheritParentTools = true
            }
        };
    }

    /// <inheritdoc />
    public SubagentDefinition GetProfile(SubagentType type)
    {
        if (_profiles.TryGetValue(type, out var profile))
        {
            return profile;
        }

        throw new ArgumentOutOfRangeException(
            nameof(type),
            type,
            $"No built-in profile registered for subagent type '{type}'.");
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<SubagentType, SubagentDefinition> GetAllProfiles()
    {
        return _profiles;
    }
}

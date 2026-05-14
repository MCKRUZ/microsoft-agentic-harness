using Domain.AI.Agents;
using Domain.AI.Governance;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Resolves the effective autonomy tier for an agent.
/// Accepts <see cref="SubagentType"/> because <see cref="ISubagentProfileRegistry"/>
/// is keyed by type, not agent ID.
/// </summary>
public interface IAutonomyTierResolver
{
    /// <summary>Resolves the tier by looking up the agent profile from the registry.</summary>
    AutonomyLevel Resolve(SubagentType agentType);

    /// <summary>Resolves the tier directly from the subagent definition.</summary>
    AutonomyLevel Resolve(SubagentDefinition definition);
}

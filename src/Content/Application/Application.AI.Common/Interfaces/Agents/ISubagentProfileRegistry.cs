using Domain.AI.Agents;

namespace Application.AI.Common.Interfaces.Agents;

/// <summary>
/// Registry of built-in subagent profiles. Each profile defines a predefined
/// <see cref="SubagentDefinition"/> for common agent types (Explore, Plan, Verify, Execute).
/// </summary>
public interface ISubagentProfileRegistry
{
    /// <summary>Gets the predefined definition for a built-in subagent type.</summary>
    /// <param name="type">The subagent type to retrieve.</param>
    /// <returns>The predefined subagent definition.</returns>
    SubagentDefinition GetProfile(SubagentType type);

    /// <summary>Gets all registered profiles.</summary>
    /// <returns>A dictionary mapping each subagent type to its definition.</returns>
    IReadOnlyDictionary<SubagentType, SubagentDefinition> GetAllProfiles();
}

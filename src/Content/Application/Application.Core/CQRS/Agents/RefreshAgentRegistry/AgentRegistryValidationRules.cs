namespace Application.Core.CQRS.Agents.RefreshAgentRegistry;

/// <summary>Shared validation constants for agent registry operator commands.</summary>
public static class AgentRegistryValidationRules
{
    /// <summary>Upper bound on <see cref="RefreshAgentRegistryCommand.CallerId"/>'s length.</summary>
    public const int MaxCallerIdLength = 256;
}

using Domain.AI.Governance;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Evaluates agent actions against declarative governance policies.
/// Implementations wrap an external policy engine (e.g., AGT) behind this harness-owned interface.
/// </summary>
public interface IGovernancePolicyEngine
{
    /// <summary>
    /// Evaluates whether an agent is permitted to invoke a specific tool with given arguments.
    /// </summary>
    /// <param name="agentId">The agent requesting the action.</param>
    /// <param name="toolName">The tool being invoked.</param>
    /// <param name="arguments">Optional tool arguments for context-aware policy evaluation.</param>
    /// <returns>A governance decision indicating whether the action is allowed.</returns>
    GovernanceDecision EvaluateToolCall(string agentId, string toolName, IReadOnlyDictionary<string, object>? arguments = null);

    /// <summary>
    /// Loads a YAML policy file into the engine at runtime.
    /// </summary>
    /// <param name="yamlPath">Absolute path to the YAML policy file.</param>
    void LoadPolicyFile(string yamlPath);

    /// <summary>Gets whether the governance engine has any policies loaded.</summary>
    bool HasPolicies { get; }
}

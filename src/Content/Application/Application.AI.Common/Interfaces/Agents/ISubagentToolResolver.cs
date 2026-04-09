using Domain.AI.Agents;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Interfaces.Agents;

/// <summary>
/// Resolves the tool pool for a subagent by filtering the parent's tools
/// through the subagent's allowlist and denylist.
/// </summary>
public interface ISubagentToolResolver
{
    /// <summary>
    /// Filters the parent's tool pool according to the subagent definition.
    /// </summary>
    /// <param name="definition">The subagent's configuration including allow/deny lists.</param>
    /// <param name="parentTools">The parent agent's available tools.</param>
    /// <returns>The filtered tool set for the subagent.</returns>
    IReadOnlyList<AITool> ResolveToolsForSubagent(
        SubagentDefinition definition,
        IReadOnlyList<AITool> parentTools);
}

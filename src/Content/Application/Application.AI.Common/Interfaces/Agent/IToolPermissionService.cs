namespace Application.AI.Common.Interfaces.Agent;

/// <summary>
/// Resolves whether a specific agent is permitted to use a specific tool.
/// Tool permissions are defined in the agent manifest (AGENT.md) and loaded
/// at agent startup.
/// </summary>
/// <remarks>
/// Implementation lives in Infrastructure. This interface enables the
/// <c>ToolPermissionBehavior</c> to enforce agent-level tool ACLs without
/// depending on manifest parsing details.
/// </remarks>
public interface IToolPermissionService
{
    /// <summary>
    /// Checks whether the specified agent is allowed to invoke the specified tool.
    /// </summary>
    /// <param name="agentId">The agent requesting access.</param>
    /// <param name="toolName">The tool name or key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the agent may use the tool; <c>false</c> otherwise.</returns>
    ValueTask<bool> IsToolAllowedAsync(string agentId, string toolName, CancellationToken cancellationToken);
}

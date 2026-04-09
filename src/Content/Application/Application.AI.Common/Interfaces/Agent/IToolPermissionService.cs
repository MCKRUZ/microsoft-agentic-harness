using Domain.AI.Permissions;

namespace Application.AI.Common.Interfaces.Agent;

/// <summary>
/// Resolves whether a specific agent is permitted to use a specific tool.
/// Uses a 3-phase resolution algorithm: Deny gates -> Ask rules -> Allow rules.
/// </summary>
/// <remarks>
/// Implementation lives in Infrastructure. This interface enables the
/// <c>ToolPermissionBehavior</c> to enforce agent-level tool ACLs without
/// depending on manifest parsing details.
/// </remarks>
public interface IToolPermissionService
{
    /// <summary>
    /// Resolves the full permission decision for a tool invocation, including the matched rule and reason.
    /// </summary>
    /// <param name="agentId">The agent requesting access.</param>
    /// <param name="toolName">The tool name or key.</param>
    /// <param name="operation">Optional operation within the tool (e.g., "read", "write").</param>
    /// <param name="parameters">Optional tool execution parameters (may contain file paths for safety gate checks).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="PermissionDecision"/> containing the behavior, reason, and matched rule.</returns>
    ValueTask<PermissionDecision> ResolvePermissionAsync(
        string agentId,
        string toolName,
        string? operation = null,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether the specified agent is allowed to invoke the specified tool.
    /// Backward-compatible convenience wrapper over <see cref="ResolvePermissionAsync"/>.
    /// </summary>
    /// <param name="agentId">The agent requesting access.</param>
    /// <param name="toolName">The tool name or key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the agent may use the tool; <c>false</c> otherwise.</returns>
    ValueTask<bool> IsToolAllowedAsync(
        string agentId,
        string toolName,
        CancellationToken cancellationToken);
}

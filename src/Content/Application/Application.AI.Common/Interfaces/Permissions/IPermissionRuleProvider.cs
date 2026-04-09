using Domain.AI.Permissions;

namespace Application.AI.Common.Interfaces.Permissions;

/// <summary>
/// Loads permission rules from a specific source (manifest, config, session).
/// Multiple providers are registered and aggregated during resolution.
/// </summary>
public interface IPermissionRuleProvider
{
    /// <summary>Gets the source type this provider supplies rules for.</summary>
    PermissionRuleSource Source { get; }

    /// <summary>
    /// Loads all permission rules from this source for the given agent.
    /// </summary>
    /// <param name="agentId">The agent requesting permission rules.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An ordered list of permission rules from this source.</returns>
    Task<IReadOnlyList<ToolPermissionRule>> GetRulesAsync(
        string agentId,
        CancellationToken cancellationToken = default);
}

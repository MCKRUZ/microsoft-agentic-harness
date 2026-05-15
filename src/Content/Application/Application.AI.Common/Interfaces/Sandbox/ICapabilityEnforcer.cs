using Domain.AI.Sandbox;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Sandbox;

/// <summary>
/// Enforces capability-based permission checks before tool execution.
/// Resolves a tool's <see cref="ToolPermissionProfile"/> from attributes and configuration,
/// then verifies that granted capabilities satisfy the tool's requirements.
/// </summary>
public interface ICapabilityEnforcer
{
    /// <summary>
    /// Resolves the permission profile for a tool by merging compile-time attribute
    /// declarations with runtime configuration overrides.
    /// </summary>
    /// <param name="toolName">The keyed DI tool name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved permission profile.</returns>
    Task<ToolPermissionProfile> ResolveProfileAsync(string toolName, CancellationToken ct);

    /// <summary>
    /// Enforces that the granted capabilities satisfy the tool's requirements,
    /// including path and host allow/deny checks.
    /// </summary>
    /// <param name="toolName">The keyed DI tool name.</param>
    /// <param name="grantedCapabilities">Capabilities currently available.</param>
    /// <param name="requestedPaths">Filesystem paths the tool wants to access.</param>
    /// <param name="requestedHosts">Network hosts the tool wants to contact.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success if allowed; failure with the specific violation reason.</returns>
    Task<Result> EnforceAsync(
        string toolName,
        ToolCapability grantedCapabilities,
        IReadOnlyList<string>? requestedPaths = null,
        IReadOnlyList<string>? requestedHosts = null,
        CancellationToken ct = default);
}

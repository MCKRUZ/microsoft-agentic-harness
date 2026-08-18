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
    /// Enforces that the granted capabilities satisfy the tool's requirements, honoring any
    /// per-tool <c>DeniedCapabilities</c> override (#405).
    /// </summary>
    /// <param name="toolName">The keyed DI tool name.</param>
    /// <param name="grantedCapabilities">Capabilities currently available.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success if allowed; failure with the specific violation reason.</returns>
    /// <remarks>
    /// <strong>Breaking change (#405):</strong> this method previously took two additional optional
    /// parameters, <c>requestedPaths</c>/<c>requestedHosts</c>, for the per-tool filesystem-path and
    /// network-host allow/deny scoping <see cref="ToolPermissionProfile"/> used to carry. That scoping
    /// was removed as dead configuration — no production caller ever passed those arguments, and no
    /// sandbox launch preparer (Docker or Process) ever read the corresponding profile fields either,
    /// so the mechanism was inert end to end, not just the parameters. A consumer built against the
    /// pre-#405 signature needs to drop those arguments on upgrade; the capability model
    /// (<see cref="ToolCapability"/>) is unaffected. Re-introducing per-tool path/host scoping is
    /// tracked separately — see the GitHub issue this PR's description links.
    /// </remarks>
    Task<Result> EnforceAsync(
        string toolName,
        ToolCapability grantedCapabilities,
        CancellationToken ct = default);
}

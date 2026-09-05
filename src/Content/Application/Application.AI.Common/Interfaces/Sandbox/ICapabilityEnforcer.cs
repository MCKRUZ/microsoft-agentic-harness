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
    /// per-tool <c>DeniedCapabilities</c> override (#405), and — when the tool's resolved
    /// <see cref="ToolPermissionProfile"/> has any path/host scoping configured — that
    /// <paramref name="requestedPaths"/>/<paramref name="requestedHosts"/> fall within it (#418).
    /// </summary>
    /// <param name="toolName">The keyed DI tool name.</param>
    /// <param name="grantedCapabilities">Capabilities currently available.</param>
    /// <param name="requestedPaths">
    /// The filesystem paths this call actually named, extracted before this method runs — see
    /// <c>ToolCallResourceRequest</c>'s remarks for why <see langword="null"/> (resource usage could
    /// not be determined) and an empty list (determined, and there genuinely is none) are refused and
    /// allowed respectively, never treated the same. A profile with no path scoping configured ignores
    /// this parameter entirely regardless of its value.
    /// </param>
    /// <param name="requestedHosts">As <paramref name="requestedPaths"/>, for network hosts.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success if allowed; failure with the specific violation reason.</returns>
    Task<Result> EnforceAsync(
        string toolName,
        ToolCapability grantedCapabilities,
        IReadOnlyList<string>? requestedPaths = null,
        IReadOnlyList<string>? requestedHosts = null,
        CancellationToken ct = default);
}

namespace Application.AI.Common.Interfaces.Bundles;

/// <summary>
/// Tears down a bundle's own MCP server registrations when its handle is evicted — the deregistration
/// counterpart to the registration <c>BundleStagingService</c> performs at staging time. A bundle's
/// servers are registered under a bundle-scoped, namespaced key
/// (<c>StagedBundle.McpServerNames</c>) that no other bundle or host server shares, so deregistering
/// them can never affect anything outside the one bundle whose handle was removed.
/// </summary>
public interface IBundleMcpServerRegistrar
{
    /// <summary>
    /// Removes each of <paramref name="serverNames"/> from the shared MCP server configuration and
    /// disconnects any live client connected under that name. Idempotent — a name that was never
    /// registered, or was already deregistered, is a no-op for that name. Never throws for a bad or
    /// already-gone name; a connection-level failure while disconnecting is logged and swallowed, not
    /// propagated, since this runs on cleanup paths that must not fail the caller.
    /// </summary>
    /// <param name="serverNames">The bundle-scoped, namespaced server names to deregister.</param>
    Task DeregisterAsync(IReadOnlyList<string> serverNames);
}

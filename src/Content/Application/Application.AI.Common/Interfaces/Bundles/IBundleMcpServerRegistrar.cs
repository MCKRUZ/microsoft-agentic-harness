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

    /// <summary>
    /// Tears down one run's own stdio MCP sessions for <paramref name="serverNames"/>, called when that
    /// run completes — the run-scoped counterpart to <see cref="DeregisterAsync"/>'s handle-scoped
    /// teardown.
    /// </summary>
    /// <remarks>
    /// <strong>Deliberately does not touch <c>IBundleOwnedMcpServerRegistry</c>.</strong> A run ending
    /// does not mean the bundle's handle is gone — the server's definition must stay registered so the
    /// next run against the same staged handle can still find it and start its own fresh session.
    /// Disconnecting only the client is what makes this safe to call unconditionally at the end of every
    /// run, whether or not that run actually contacted the server (idempotent, same as
    /// <see cref="DeregisterAsync"/>).
    /// </remarks>
    /// <param name="serverNames">The bundle-scoped, namespaced server names this run's staged bundle declares.</param>
    /// <param name="runId">The completed run's job id — the same value that scoped its sessions.</param>
    Task DisconnectRunScopedAsync(IReadOnlyList<string> serverNames, string runId);
}

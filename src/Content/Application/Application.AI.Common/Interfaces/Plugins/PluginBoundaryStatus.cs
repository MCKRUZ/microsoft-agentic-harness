namespace Application.AI.Common.Interfaces.Plugins;

/// <summary>
/// A plugin's <c>AllowedTools</c>/<c>DeniedTools</c> boundary trust state (#524).
/// </summary>
/// <remarks>
/// Deliberately three states, not a boolean: collapsing "still waiting to find out" into "not yet
/// proven broken" is what let a plugin boundary stay trusted indefinitely when a dependent MCP server
/// was never queried in a given process's lifetime — the exact gap that made the original
/// existence-check ineffective on a multi-server host. <see cref="Faulted"/> is terminal (no
/// transition back to <see cref="Verified"/>); <see cref="Pending"/> is the fail-closed default for
/// anything not yet proven either way.
/// </remarks>
public enum PluginBoundaryStatus
{
    /// <summary>No <c>AllowedTools</c>/<c>DeniedTools</c> entry needs existence verification, or
    /// every entry that did has been confirmed to match a real tool.</summary>
    Verified,

    /// <summary>At least one entry still awaits an MCP server's tool list before it can be confirmed
    /// or refuted — treated as untrusted (deny all) until it resolves.</summary>
    Pending,

    /// <summary>At least one entry has been proven to match no real tool. Terminal for the process
    /// lifetime.</summary>
    Faulted,
}

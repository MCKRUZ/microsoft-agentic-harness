namespace Application.AI.Common.Interfaces.Plugins;

/// <summary>
/// Runtime query interface for loaded plugins.
/// </summary>
public interface IPluginRegistry
{
    /// <summary>All currently loaded plugins.</summary>
    IReadOnlyList<LoadedPlugin> GetLoadedPlugins();

    /// <summary>Get a specific loaded plugin by name.</summary>
    LoadedPlugin? GetPlugin(string name);

    /// <summary>Whether a plugin is loaded and active.</summary>
    bool IsLoaded(string name);

    /// <summary>Registers a loaded plugin.</summary>
    void Register(LoadedPlugin plugin);

    /// <summary>
    /// <paramref name="pluginName"/>'s tool boundary (<c>AllowedTools</c>/<c>DeniedTools</c>) trust
    /// state — see <see cref="PluginBoundaryStatus"/>'s remarks for why this is three states, not a
    /// boolean. Defaults to <see cref="PluginBoundaryStatus.Verified"/> for a plugin never marked
    /// otherwise (no boundary declaration, or nothing to verify).
    /// </summary>
    PluginBoundaryStatus GetBoundaryStatus(string pluginName);

    /// <summary>
    /// Marks <paramref name="pluginName"/>'s tool boundary <see cref="PluginBoundaryStatus.Pending"/>:
    /// at least one <c>AllowedTools</c>/<c>DeniedTools</c> entry could not be resolved against any
    /// already-known tool and awaits an MCP server's tool list (#524). Treated fail-closed exactly
    /// like <see cref="PluginBoundaryStatus.Faulted"/> until it resolves — see
    /// <see cref="MarkBoundaryVerified"/>/<see cref="MarkBoundaryFaulted"/>.
    /// </summary>
    /// <param name="pluginName">The plugin whose boundary has an unresolved entry.</param>
    void MarkBoundaryPending(string pluginName);

    /// <summary>
    /// Marks <paramref name="pluginName"/>'s tool boundary <see cref="PluginBoundaryStatus.Verified"/>:
    /// every entry that was <see cref="PluginBoundaryStatus.Pending"/> has now been confirmed to match
    /// a real tool. A no-op — never a downgrade — if the plugin is already
    /// <see cref="PluginBoundaryStatus.Faulted"/>: that state is terminal, and a caller resolving a
    /// pending entry has no way to know whether some OTHER entry already faulted this plugin through
    /// a different call, so the registry itself must be the one place this can't be gotten wrong.
    /// </summary>
    /// <param name="pluginName">The plugin whose boundary is now fully verified.</param>
    void MarkBoundaryVerified(string pluginName);

    /// <summary>
    /// Marks <paramref name="pluginName"/>'s tool boundary <see cref="PluginBoundaryStatus.Faulted"/>:
    /// at least one of its <c>AllowedTools</c>/<c>DeniedTools</c> entries has been proven to match no
    /// real tool (#524). A boundary that can't be trusted is treated fail-closed — <c>ToolChainBuilder</c>
    /// denies every tool for a faulted plugin rather than run with a partially-broken policy, since a
    /// typo in <c>DeniedTools</c> (documented as bypass-immune) silently defeats that guarantee
    /// otherwise. Terminal: once faulted, always faulted for the process lifetime.
    /// </summary>
    /// <param name="pluginName">The plugin whose boundary is faulted.</param>
    /// <param name="reason">Human-readable reason, for logging/diagnostics.</param>
    void MarkBoundaryFaulted(string pluginName, string reason);
}

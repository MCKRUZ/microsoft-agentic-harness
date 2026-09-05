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
    /// Whether <paramref name="pluginName"/>'s tool boundary (<c>AllowedTools</c>/<c>DeniedTools</c>)
    /// has been marked faulted by <see cref="MarkBoundaryFaulted"/> — see that method's remarks.
    /// </summary>
    bool IsBoundaryFaulted(string pluginName);

    /// <summary>
    /// Marks <paramref name="pluginName"/>'s tool boundary as faulted: at least one of its
    /// <c>AllowedTools</c>/<c>DeniedTools</c> entries has been proven to match no real tool (#524).
    /// A boundary that can't be trusted is treated fail-closed — <c>ToolChainBuilder</c> denies every
    /// tool for a faulted plugin rather than run with a partially-broken policy, since a typo in
    /// <c>DeniedTools</c> (documented as bypass-immune) silently defeats that guarantee otherwise.
    /// </summary>
    /// <param name="pluginName">The plugin whose boundary is faulted.</param>
    /// <param name="reason">Human-readable reason, for logging/diagnostics.</param>
    void MarkBoundaryFaulted(string pluginName, string reason);
}

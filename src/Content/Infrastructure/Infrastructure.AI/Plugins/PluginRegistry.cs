using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Plugins;

namespace Infrastructure.AI.Plugins;

/// <summary>
/// Thread-safe in-memory registry of loaded plugins.
/// </summary>
public sealed class PluginRegistry : IPluginRegistry
{
    private readonly ConcurrentDictionary<string, LoadedPlugin> _plugins =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, PluginBoundaryStatus> _boundaryStatus =
        new(StringComparer.OrdinalIgnoreCase);

    // Diagnostics only — GetBoundaryStatus is the source of truth for enforcement. Kept separate
    // from _boundaryStatus's PluginBoundaryStatus.Faulted entries rather than folded into a richer
    // value type there, since nothing besides logging needs the reason string.
    private readonly ConcurrentDictionary<string, string> _faultReasons =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IReadOnlyList<LoadedPlugin> GetLoadedPlugins() =>
        _plugins.Values.ToList();

    /// <inheritdoc />
    public LoadedPlugin? GetPlugin(string name) =>
        _plugins.GetValueOrDefault(name);

    /// <inheritdoc />
    public bool IsLoaded(string name) =>
        _plugins.TryGetValue(name, out var plugin) && plugin.Status == PluginLoadStatus.Loaded;

    /// <inheritdoc />
    public void Register(LoadedPlugin plugin) =>
        _plugins[plugin.Name] = plugin;

    /// <inheritdoc />
    public PluginBoundaryStatus GetBoundaryStatus(string pluginName) =>
        _boundaryStatus.GetValueOrDefault(pluginName, PluginBoundaryStatus.Verified);

    /// <inheritdoc />
    public void MarkBoundaryPending(string pluginName) =>
        _boundaryStatus[pluginName] = PluginBoundaryStatus.Pending;

    /// <inheritdoc />
    public void MarkBoundaryVerified(string pluginName) =>
        // Never downgrades Faulted (terminal) — see this method's interface remarks. The
        // AddOrUpdate factory re-reads the current value under the dictionary's own atomicity
        // rather than a separate check-then-set, so a concurrent MarkBoundaryFaulted for the same
        // plugin can't race this into wrongly clearing it.
        _boundaryStatus.AddOrUpdate(
            pluginName,
            PluginBoundaryStatus.Verified,
            (_, current) => current == PluginBoundaryStatus.Faulted ? current : PluginBoundaryStatus.Verified);

    /// <inheritdoc />
    public void MarkBoundaryFaulted(string pluginName, string reason)
    {
        _boundaryStatus[pluginName] = PluginBoundaryStatus.Faulted;
        _faultReasons[pluginName] = reason;
    }
}

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
        // Never downgrades Faulted (terminal) — mirrors MarkBoundaryVerified below (#524 round-2
        // code-review: this method had no such guard, an asymmetry with no live caller today since
        // Seed, its only caller, runs once per plugin per startup — but the registry is the shared
        // trust boundary, not any one caller's discipline, so it should hold regardless of caller count.
        _boundaryStatus.AddOrUpdate(
            pluginName,
            PluginBoundaryStatus.Pending,
            (_, current) => current == PluginBoundaryStatus.Faulted ? current : PluginBoundaryStatus.Pending);

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
    public void MarkBoundaryFaulted(string pluginName, string reason) =>
        // reason is a human-readable summary of facts (plugin/list-kind/tool-name) the caller already
        // surfaces separately at the point of fault — McpToolProvider logs the violation list at
        // Critical, PluginToolBoundaryStartupValidator throws with the same details — so nothing here
        // needs it back. Deliberately not stored (#524 round-2 code-review: an earlier version kept a
        // _faultReasons dictionary "for diagnostics" that nothing ever actually read).
        _boundaryStatus[pluginName] = PluginBoundaryStatus.Faulted;
}

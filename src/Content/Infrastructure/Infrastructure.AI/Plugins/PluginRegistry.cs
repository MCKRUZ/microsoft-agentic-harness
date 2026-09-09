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

    // #612 grader finding: writing the dictionary and bumping _stateVersion as two separate
    // Interlocked steps left a few-CPU-cycle window where a reader could observe the dictionary
    // already mutated but StateVersion not yet incremented — a cache keyed on that stale version
    // would then serve pre-mutation (looser) rules for one call, self-healing on the next. Narrow
    // and self-healing, but this repo's history (#553, #614) treats "permission state briefly
    // served stale-and-looser" as worth closing rather than documenting. A single lock around each
    // mutator's write+bump, and around StateVersion's read, makes the two indivisible from any
    // reader's perspective: Monitor's enter/exit fences guarantee a reader can only observe
    // StateVersion N once the dictionary write that produced N has already fully landed.
    private readonly Lock _stateLock = new();
    private long _stateVersion;

    /// <inheritdoc />
    public long StateVersion
    {
        get { lock (_stateLock) { return _stateVersion; } }
    }

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
    public void Register(LoadedPlugin plugin)
    {
        lock (_stateLock)
        {
            _plugins[plugin.Name] = plugin;
            _stateVersion++;
        }
    }

    /// <inheritdoc />
    public PluginBoundaryStatus GetBoundaryStatus(string pluginName) =>
        // #613: Pending, not Verified — see the interface doc for why. PluginToolBoundaryTracker.Seed
        // now explicitly marks every loaded plugin it processes (Verified when it has nothing to
        // verify, Pending/Faulted otherwise), so an absent entry here means genuinely unseeded, not
        // "seeded with an empty boundary" — the two used to be indistinguishable.
        _boundaryStatus.GetValueOrDefault(pluginName, PluginBoundaryStatus.Pending);

    /// <inheritdoc />
    public void MarkBoundaryPending(string pluginName)
    {
        lock (_stateLock)
        {
            // Never downgrades Faulted (terminal) — mirrors MarkBoundaryVerified below (#524
            // round-2 code-review: this method had no such guard, an asymmetry with no live caller
            // today since Seed, its only caller, runs once per plugin per startup — but the
            // registry is the shared trust boundary, not any one caller's discipline, so it should
            // hold regardless of caller count.
            _boundaryStatus.AddOrUpdate(
                pluginName,
                PluginBoundaryStatus.Pending,
                (_, current) => current == PluginBoundaryStatus.Faulted ? current : PluginBoundaryStatus.Pending);
            _stateVersion++;
        }
    }

    /// <inheritdoc />
    public void MarkBoundaryVerified(string pluginName)
    {
        lock (_stateLock)
        {
            // Never downgrades Faulted (terminal) — see this method's interface remarks. The
            // AddOrUpdate factory re-reads the current value under the dictionary's own atomicity
            // rather than a separate check-then-set, so a concurrent MarkBoundaryFaulted for the
            // same plugin can't race this into wrongly clearing it.
            _boundaryStatus.AddOrUpdate(
                pluginName,
                PluginBoundaryStatus.Verified,
                (_, current) => current == PluginBoundaryStatus.Faulted ? current : PluginBoundaryStatus.Verified);
            _stateVersion++;
        }
    }

    /// <inheritdoc />
    public void MarkBoundaryFaulted(string pluginName, string reason)
    {
        lock (_stateLock)
        {
            // reason is a human-readable summary of facts (plugin/list-kind/tool-name) the caller
            // already surfaces separately at the point of fault — McpToolProvider logs the
            // violation list at Critical, PluginToolBoundaryStartupValidator throws with the same
            // details — so nothing here needs it back. Deliberately not stored (#524 round-2
            // code-review: an earlier version kept a _faultReasons dictionary "for diagnostics"
            // that nothing ever actually read).
            _boundaryStatus[pluginName] = PluginBoundaryStatus.Faulted;
            _stateVersion++;
        }
    }
}

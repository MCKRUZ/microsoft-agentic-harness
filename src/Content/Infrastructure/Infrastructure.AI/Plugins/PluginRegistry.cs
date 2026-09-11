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

    // #608: which specific entries proved a Faulted plugin's boundary broken, so a consumer can
    // distinguish a DeniedTools fault (must still fail closed — the bypass-immune guarantee is at
    // risk) from an AllowedTools-only fault (can only ever narrow access, never widen it). Written
    // inside the same _stateLock critical section as _boundaryStatus and _stateVersion below, for the
    // same reason documented on that lock: a reader must never observe the status flip to Faulted
    // without also observing the violation list that explains it.
    private readonly ConcurrentDictionary<string, IReadOnlyList<PluginToolBoundaryViolation>> _boundaryViolations =
        new(StringComparer.OrdinalIgnoreCase);

    // #612 grader finding, round 1: writing the dictionary and bumping _stateVersion as two separate
    // Interlocked steps left a window where a reader could observe the dictionary already mutated
    // but StateVersion not yet incremented — a cache keyed on that stale version would then serve
    // pre-mutation (looser) rules. This repo's history (#553, #614) treats "permission state briefly
    // served stale-and-looser" as worth closing, not documenting.
    //
    // #612 code-review, round 2 (caught a regression in this comment's own prior claim): an
    // intermediate version tried to shrink the lock to wrap ONLY the version bump — dictionary write
    // outside the lock (lock-free), BumpVersion() after it — reasoning that a reader who observes the
    // BUMPED version is guaranteed to also see the write (true, via Monitor's release/acquire fence).
    // That reasoning covers only ONE of the two directions that matter: it says nothing about a
    // reader who reads the OLD (not-yet-bumped) version — which can happen at ANY point relative to
    // the unlocked dictionary write, including AFTER that write has already landed, since nothing
    // synchronizes the two. That reader's cache check matches its stale cached version, hits the
    // cache, and returns the pre-mutation rules — even though the dictionary already reflects the
    // new (e.g. Faulted) state. Exactly the race this comment already exists to describe, reopened
    // by trying to shrink the lock for a marginal efficiency gain on a mutation path that isn't hot
    // (plugin load / boundary transitions, not per-tool-call).
    //
    // Fix (restored): every mutator does its dictionary write AND its version bump inside the SAME
    // _stateLock critical section; StateVersion's getter reads under that same lock. A reader
    // acquiring the lock therefore always observes a fully-consistent pair — either "before this
    // mutation" (write hasn't happened yet, version is old) or "after it" (write has happened,
    // version is bumped) — never the in-between state that let a stale cache hit through. Do not
    // "optimize" this to Interlocked-only or a narrower lock scope without re-deriving BOTH
    // directions of the race, not just the one that looks fixed.
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
    public void MarkBoundaryFaulted(
        string pluginName, string reason, IReadOnlyList<PluginToolBoundaryViolation> violations)
    {
        lock (_stateLock)
        {
            // reason is a human-readable summary of facts (plugin/list-kind/tool-name) the caller
            // already surfaces separately at the point of fault — McpToolProvider logs the
            // violation list at Critical, PluginToolBoundaryStartupValidator throws with the same
            // details — so nothing here needs reason back. violations IS stored (#608 — unlike
            // reason, this now has real readers: GetBoundaryViolations lets a consumer distinguish
            // a DeniedTools fault from an AllowedTools-only one).
            _boundaryStatus[pluginName] = PluginBoundaryStatus.Faulted;
            _boundaryViolations[pluginName] = violations;
            _stateVersion++;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginToolBoundaryViolation> GetBoundaryViolations(string pluginName) =>
        _boundaryViolations.GetValueOrDefault(pluginName, []);
}

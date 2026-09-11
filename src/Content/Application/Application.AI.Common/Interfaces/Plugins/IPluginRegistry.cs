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
    /// boolean. Defaults to <see cref="PluginBoundaryStatus.Pending"/> for a plugin never marked at
    /// all (#613).
    /// </summary>
    /// <remarks>
    /// <strong>The default is fail-closed, deliberately not Verified.</strong> A plugin with no
    /// boundary declaration, or one whose every entry is a known first-party name, IS positively
    /// marked <see cref="PluginBoundaryStatus.Verified"/> — by <c>PluginToolBoundaryTracker.Seed</c>,
    /// explicitly, every time it processes a loaded plugin, even when there is nothing to verify (see
    /// its remarks). So an absent entry here means one specific thing: <c>Seed</c> has not processed
    /// this plugin yet — most plausibly a caller racing ahead of
    /// <c>PluginToolBoundaryStartupValidator.StartAsync</c>, which is the only place <c>Seed</c> is
    /// called from. <c>ToolChainBuilder.ApplyPluginBoundaryIfPluginSkill</c> trusts whatever this
    /// method returns with no separate "has Seed run yet" check of its own — before #613, that race
    /// window defaulted to Verified, meaning a genuinely never-checked <c>DeniedTools</c> entry (which
    /// is a silent no-op until proven to match a real tool — see <see cref="MarkBoundaryFaulted"/>)
    /// would be trusted and applied as if already proven safe. Defaulting to Pending closes that by
    /// construction: the caller denies all tools for a plugin in this state, regardless of whether the
    /// race is even reachable in a given host's actual startup ordering — the same fail-closed
    /// treatment a <see cref="PluginBoundaryStatus.Faulted"/> boundary gets when its violations aren't
    /// (or can't be) proven confined to <c>AllowedTools</c>. Since #608, that "exactly as Faulted"
    /// equivalence is no longer literal in every case: a Faulted boundary whose violations ARE proven
    /// confined to <c>AllowedTools</c> runs the caller's normal filter instead of denying everything,
    /// while Pending still denies everything unconditionally — Pending was deliberately left out of
    /// that narrowing (see <c>PluginPermissionRuleProvider.BoundaryDemandsAgentWideFailClosed</c>'s
    /// remarks for why: it's transient by nature, not a #608 scope decision).
    /// </remarks>
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
    /// real tool (#524). A boundary that can't be trusted is treated fail-closed by default —
    /// <c>ToolChainBuilder</c> denies every tool for a faulted plugin rather than run with a
    /// partially-broken policy, since a typo in <c>DeniedTools</c> (documented as bypass-immune)
    /// silently defeats that guarantee otherwise. Terminal: once faulted, always faulted for the
    /// process lifetime.
    /// </summary>
    /// <param name="pluginName">The plugin whose boundary is faulted.</param>
    /// <param name="reason">Human-readable reason, for logging/diagnostics.</param>
    /// <param name="violations">
    /// The specific entries that proved fake (#608). Callers that need to distinguish a
    /// <c>DeniedTools</c> fault (still must fail closed everywhere — the bypass-immune guarantee is at
    /// risk) from an <c>AllowedTools</c>-only fault (can never widen access, so the narrower
    /// consequence of just excluding that one name is safe) read this back via
    /// <see cref="GetBoundaryViolations"/>. Required, not optional: the only production caller
    /// (<c>PluginToolBoundaryTracker</c>) already computes this list at both places it calls this
    /// method, so there is no case where passing it is a real burden — and an optional parameter would
    /// let a future call site silently keep the registry violations-blind for that plugin, defeating
    /// the whole point of storing this.
    /// </param>
    void MarkBoundaryFaulted(
        string pluginName, string reason, IReadOnlyList<PluginToolBoundaryViolation> violations);

    /// <summary>
    /// The specific boundary entries that proved <paramref name="pluginName"/>'s tool boundary
    /// <see cref="PluginBoundaryStatus.Faulted"/> (#608) — empty when the plugin isn't
    /// <see cref="PluginBoundaryStatus.Faulted"/>, or when it is but no violation detail was recorded
    /// for it.
    /// </summary>
    /// <remarks>
    /// A caller deciding how broadly to fail closed on a Faulted plugin should treat an empty result
    /// here the same as "at least one violation is <c>DeniedTools</c>" — i.e. fail closed on
    /// uncertainty, not just on a confirmed hit. The only way this comes back empty for a genuinely
    /// Faulted plugin is a registry implementation (or a test double) that never recorded violations at
    /// all, and that absence of information is exactly the case #524's own reasoning treats as
    /// dangerous-until-proven-otherwise.
    /// </remarks>
    /// <param name="pluginName">The plugin to query.</param>
    IReadOnlyList<PluginToolBoundaryViolation> GetBoundaryViolations(string pluginName);

    /// <summary>
    /// Monotonically increasing counter, bumped by every mutation (<see cref="Register"/>,
    /// <see cref="MarkBoundaryPending"/>, <see cref="MarkBoundaryVerified"/>,
    /// <see cref="MarkBoundaryFaulted"/>).
    /// </summary>
    /// <remarks>
    /// A consumer whose derived state depends on the full loaded-plugin set and every plugin's
    /// boundary status — <c>PluginPermissionRuleProvider.GetRulesAsync</c>, called on every
    /// tool-permission resolution — can cache that derived state and detect staleness by comparing
    /// this value, instead of recomputing on every call (#611) or wiring a bespoke
    /// change-notification event. Deliberately coarse: it bumps on any mutation regardless of
    /// whether the mutation actually changed anything observable (e.g. re-marking an already-Verified
    /// plugin Verified), which only costs an occasional unnecessary recompute — the alternative, a
    /// missed bump, would serve stale cached state indefinitely.
    /// </remarks>
    long StateVersion { get; }
}

using System.Collections.Concurrent;

namespace Domain.Common.Config.AI.MCP;

/// <summary>
/// Runtime-only store for a bundle's own MCP server definitions — deliberately a distinct type from
/// <see cref="McpServersConfig"/>, not a second instance of it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> A bundle (untrusted, user-uploaded content) can declare its own MCP
/// server. Registering that definition into the shared, singleton <see cref="McpServersConfig"/> — the
/// same dictionary trusted, host-admin-configured servers live in — let it leak into every consumer that
/// enumerates that dictionary (<c>McpConnectionManager.GetConfiguredServerNames</c> and everything built
/// on it: <c>McpToolProvider.GetAllToolsAsync</c>/<c>GetToolByNameAsync</c>, the MCP REST endpoints), with
/// no bundle-provenance filter and no <c>CapabilityEnvelope</c> check — so an ordinary, non-bundle agent
/// conversation would pull in an attacker's tools under their bare, attacker-chosen names. This registry
/// closes that by construction: a bundle-owned definition is never reachable from
/// <c>GetConfiguredServerNames()</c> at all, because it is never IN <see cref="McpServersConfig.Servers"/>.
/// Only an exact-name lookup (<c>McpConnectionManager</c>'s connect path) consults this registry, as a
/// fallback after the host dictionary misses — the path <c>ToolChainBuilder</c>'s already envelope-gated,
/// name-keyed bundle-run resolution uses.
/// </para>
/// <para>
/// <strong>Deliberately not <c>: McpServersConfig</c>.</strong> Inheriting would let a parameter typed
/// <see cref="McpServersConfig"/> still accept an instance of this type — the exact ambiguity a distinct,
/// unrelated type rules out at the compiler level. A same-typed second instance distinguished only by a
/// DI key was considered and rejected for the same reason: two adjacent same-typed constructor parameters
/// invite a silent swap in a future refactor.
/// </para>
/// <para>
/// <strong>No enumeration surface, deliberately.</strong> Unlike <see cref="McpServersConfig.Servers"/>
/// (a public, directly-enumerable <see cref="ConcurrentDictionary{TKey,TValue}"/>), this type exposes only
/// <see cref="TryAdd"/>/<see cref="TryRemove"/>/<see cref="TryGetValue"/> — no <c>Keys</c>, <c>Values</c>,
/// or enumerator. "Never reachable from an enumeration chokepoint" must hold for every future consumer
/// this registry is ever injected into, not just the ones reviewed today; a raw dictionary property would
/// make that merely a documented promise a future caller could break with a casual <c>foreach</c>. A
/// narrow, name-only API makes it true by type instead.
/// </para>
/// <para>
/// <strong>Never config-bound.</strong> Starts empty and is never touched by the options binder — unlike
/// <see cref="McpServersConfig"/>, which doubles as both an <c>appsettings.json</c>-bound options object
/// and a runtime-mutated registry, this type has exactly one role.
/// </para>
/// </remarks>
public sealed class BundleOwnedMcpServerRegistry
{
    private readonly ConcurrentDictionary<string, McpServerDefinition> _servers = new();

    /// <summary>
    /// Registers <paramref name="definition"/> under <paramref name="namespacedName"/>
    /// (<c>{BundleId}:{ServerName}</c>). Returns <see langword="false"/> without replacing anything if
    /// that name is already registered — the caller decides how to report a duplicate.
    /// </summary>
    public bool TryAdd(string namespacedName, McpServerDefinition definition) =>
        _servers.TryAdd(namespacedName, definition);

    /// <summary>
    /// Removes the entry registered under <paramref name="namespacedName"/>, if any. Idempotent: removing
    /// an unregistered name is a no-op that returns <see langword="false"/>, never an error. No caller
    /// needs the removed definition, so this returns only whether an entry was removed — not the value
    /// itself, matching this type's stated goal of exposing the narrowest usable surface.
    /// </summary>
    public bool TryRemove(string namespacedName) => _servers.TryRemove(namespacedName, out _);

    /// <summary>
    /// Resolves <paramref name="namespacedName"/> to its registered definition, if any — the ONLY way to
    /// read from this registry. There is no enumeration method by design; see this type's own remarks.
    /// </summary>
    public bool TryGetValue(string namespacedName, out McpServerDefinition? definition) =>
        _servers.TryGetValue(namespacedName, out definition);
}

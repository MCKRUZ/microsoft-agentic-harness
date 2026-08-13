using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Domain.Common.Config.AI.MCP;

/// <summary>
/// Runtime-only store for a bundle's own MCP server definitions — deliberately a distinct type from
/// <see cref="McpServersConfig"/>, not a second instance of it.
/// </summary>
/// <remarks>
/// A bundle (untrusted, user-uploaded content) can declare its own MCP server; registering it into the
/// shared, enumerable <see cref="McpServersConfig"/> would leak it into every consumer that enumerates
/// that dictionary (<c>McpConnectionManager.GetConfiguredServerNames</c> and everything built on it) with
/// no bundle-provenance check. This type closes that two ways at once: it is a distinct type — not
/// inheritance, not a DI-keyed second <see cref="McpServersConfig"/> instance — so a bundle-owned
/// definition can never be type- or key-confused with a trusted entry; and it exposes no enumeration API
/// at all (<see cref="TryAdd"/>/<see cref="TryRemove"/>/<see cref="TryGetValue"/> only), so it can never
/// become a second <c>GetConfiguredServerNames</c>-style chokepoint for any future consumer it's injected
/// into. Only an exact-name lookup (<c>McpConnectionManager</c>'s connect path) consults this registry,
/// as a fallback after the host dictionary misses — the path <c>ToolChainBuilder</c>'s already
/// envelope-gated, name-keyed bundle-run resolution uses. Never config-bound; starts empty.
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
    public bool TryGetValue(string namespacedName, [NotNullWhen(true)] out McpServerDefinition? definition) =>
        _servers.TryGetValue(namespacedName, out definition);
}

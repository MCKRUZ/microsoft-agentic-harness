using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Application.AI.Common.Interfaces.Bundles;
using Domain.Common.Config.AI.MCP;

namespace Infrastructure.AI.Bundles;

/// <inheritdoc cref="IBundleOwnedMcpServerRegistry"/>
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
public sealed class BundleOwnedMcpServerRegistry : IBundleOwnedMcpServerRegistry
{
    private readonly ConcurrentDictionary<string, McpServerDefinition> _servers = new();

    /// <inheritdoc />
    public bool TryAdd(string namespacedName, McpServerDefinition definition) =>
        _servers.TryAdd(namespacedName, definition);

    /// <inheritdoc />
    public bool TryRemove(string namespacedName) => _servers.TryRemove(namespacedName, out _);

    /// <inheritdoc />
    public bool TryGetValue(string namespacedName, [NotNullWhen(true)] out McpServerDefinition? definition) =>
        _servers.TryGetValue(namespacedName, out definition);
}

using System.Diagnostics.CodeAnalysis;
using Domain.Common.Config.AI.MCP;

namespace Application.AI.Common.Interfaces.Bundles;

/// <summary>
/// Runtime-only store for a bundle's own MCP server definitions — deliberately a distinct type from
/// <see cref="McpServersConfig"/>, not a second instance of it. See the implementation's own remarks
/// (<c>BundleOwnedMcpServerRegistry</c>) for the full isolation rationale from issue #368.
/// </summary>
/// <remarks>
/// Extracted under #374 to match every other runtime, string-keyed registry of this shape elsewhere in
/// the codebase (<c>IPluginRegistry</c>, <c>IMcpDefinitionPinStore</c>, <c>IHookRegistry</c>) —
/// interface in Application, implementation in Infrastructure — so a future durable/EF-backed
/// implementation can be swapped in without a breaking constructor-signature change across every
/// consumer. The surface is deliberately unchanged from the original concrete type: no enumeration
/// method exists, and none should be added without a deliberate, reviewed decision — a reflection test
/// (<c>BundleMcpServerIsolationTests</c>) fails the build if this interface's public member set grows
/// beyond <see cref="TryAdd"/>/<see cref="TryRemove"/>/<see cref="TryGetValue"/>.
/// </remarks>
public interface IBundleOwnedMcpServerRegistry
{
    /// <summary>
    /// Registers <paramref name="definition"/> under <paramref name="namespacedName"/>
    /// (<c>{BundleId}:{ServerName}</c>). Returns <see langword="false"/> without replacing anything if
    /// that name is already registered — the caller decides how to report a duplicate.
    /// </summary>
    bool TryAdd(string namespacedName, McpServerDefinition definition);

    /// <summary>
    /// Removes the entry registered under <paramref name="namespacedName"/>, if any. Idempotent: removing
    /// an unregistered name is a no-op that returns <see langword="false"/>, never an error.
    /// </summary>
    bool TryRemove(string namespacedName);

    /// <summary>
    /// Resolves <paramref name="namespacedName"/> to its registered definition, if any — the ONLY way to
    /// read from this registry. There is no enumeration method by design.
    /// </summary>
    bool TryGetValue(string namespacedName, [NotNullWhen(true)] out McpServerDefinition? definition);
}

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
/// <strong>Never config-bound.</strong> <see cref="Servers"/> is get-only and starts empty — unlike
/// <see cref="McpServersConfig"/>, which doubles as both an <c>appsettings.json</c>-bound options object
/// and a runtime-mutated registry, this type has exactly one role.
/// </para>
/// </remarks>
public sealed class BundleOwnedMcpServerRegistry
{
    /// <summary>
    /// The dictionary of bundle-owned MCP server definitions, keyed by their bundle-scoped namespaced
    /// name (<c>{BundleId}:{ServerName}</c>). Never enumerated by anything that publishes tools to an
    /// ordinary, non-bundle conversation — only resolved by exact name from an envelope-gated caller.
    /// </summary>
    public ConcurrentDictionary<string, McpServerDefinition> Servers { get; } = new();
}

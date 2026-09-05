using Domain.Common.Config.AI.Plugins;

namespace Application.AI.Common.Interfaces.Plugins;

/// <summary>
/// One <see cref="PluginDeclaration.AllowedTools"/>/<see cref="PluginDeclaration.DeniedTools"/>
/// entry that has been confirmed to not match any known tool — first-party or MCP-provided.
/// </summary>
/// <param name="PluginName">The plugin whose boundary declared the entry.</param>
/// <param name="ListKind">Either <c>"AllowedTools"</c> or <c>"DeniedTools"</c>, for the error message.</param>
/// <param name="ToolName">The offending entry itself.</param>
public sealed record PluginToolBoundaryViolation(string PluginName, string ListKind, string ToolName);

/// <summary>
/// Tracks whether every <c>AllowedTools</c>/<c>DeniedTools</c> entry a loaded plugin declares
/// actually matches a real tool — first-party (keyed-DI, known at startup) or MCP-provided (known
/// only once the owning server's tool list has been discovered at least once).
/// </summary>
/// <remarks>
/// See #524: a plugin boundary entry that matches nothing is a silent no-op today — most
/// dangerously for <c>DeniedTools</c>, which is documented as bypass-immune. This tracker is what
/// turns that into a loud, fail-closed fault instead. <see cref="Seed"/> resolves what's decidable
/// immediately (no MCP server is configured anywhere on the host, so nothing could ever resolve an
/// unmatched entry); <see cref="ReportServerToolsDiscovered"/> resolves the rest lazily, as MCP
/// servers are organically discovered during normal operation — never by connecting to a server
/// early just to check.
/// </remarks>
public interface IPluginToolBoundaryTracker
{
    /// <summary>
    /// Called once at startup, after plugins are loaded. Returns the entries that are immediately,
    /// provably fake — no MCP server is configured anywhere on the host, so every boundary entry
    /// must be a first-party name; anything <paramref name="isKnownFirstPartyToolName"/> rejects is a
    /// definite typo, decidable right now. Every other unresolved entry is retained internally,
    /// pending <see cref="ReportServerToolsDiscovered"/>.
    /// </summary>
    /// <param name="loadedPlugins">Every currently loaded plugin.</param>
    /// <param name="isKnownFirstPartyToolName">
    /// Bounded first-party (keyed-DI) tool-name membership check. Must match names
    /// case-insensitively, the same way <c>ToolChainBuilder.ApplyPluginToolBoundary</c> matches a
    /// boundary entry against a tool's real published name — a case-sensitive check here would
    /// falsely flag a real, just differently-cased, tool name as nonexistent.
    /// </param>
    /// <param name="allConfiguredMcpServerNames">
    /// Every enabled MCP server name configured anywhere on the host — <strong>not</strong> narrowed
    /// to any one plugin's own declared servers. A review-round finding traced
    /// <c>ToolChainBuilder.ResolveEffectiveMcpServerName</c> and confirmed a plugin skill's
    /// <c>ToolDeclaration</c> can resolve against ANY host-configured MCP server when no capability
    /// envelope restricts it — so a plugin that declares zero MCP servers of its own can still
    /// legitimately reference a host-level server's tool in its boundary. Narrowing this list to a
    /// per-plugin one previously crashed boot (or permanently denied every tool) on exactly that
    /// valid configuration.
    /// </param>
    IReadOnlyList<PluginToolBoundaryViolation> Seed(
        IReadOnlyList<LoadedPlugin> loadedPlugins,
        Func<string, bool> isKnownFirstPartyToolName,
        IReadOnlyCollection<string> allConfiguredMcpServerNames);

    /// <summary>
    /// Called every time an MCP server's tool list is successfully discovered (the existing lazy
    /// discovery path — this method never triggers a connection itself). Resolves any pending entry
    /// <paramref name="discoveredToolNames"/> now accounts for. When this was the LAST MCP server a
    /// plugin depends on to report, and that plugin still has unresolved entries left, those entries
    /// are now provably fake: this method marks the plugin's boundary faulted
    /// (<see cref="IPluginRegistry.MarkBoundaryFaulted"/>) and returns them. Returns an empty list in
    /// the common case (nothing pending for this server, or everything pending just got resolved).
    /// </summary>
    /// <param name="serverName">The namespaced (<c>{pluginName}:{serverName}</c>) MCP server name.</param>
    /// <param name="discoveredToolNames">The raw tool names the server just reported.</param>
    IReadOnlyList<PluginToolBoundaryViolation> ReportServerToolsDiscovered(
        string serverName, IReadOnlyCollection<string> discoveredToolNames);
}

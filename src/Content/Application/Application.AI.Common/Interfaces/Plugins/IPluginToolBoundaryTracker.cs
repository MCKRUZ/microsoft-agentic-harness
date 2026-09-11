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
/// The two values <see cref="PluginToolBoundaryViolation.ListKind"/> can hold. Shared so
/// <c>PluginToolBoundaryTracker</c> (which produces violations) and every consumer that reads them
/// back via <see cref="IPluginRegistry.GetBoundaryViolations"/> (#608) compare against the same
/// symbols rather than each hardcoding the literal strings.
/// </summary>
public static class PluginToolBoundaryListKind
{
    /// <summary>A <see cref="Domain.Common.Config.AI.Plugins.PluginDeclaration.AllowedTools"/> entry.</summary>
    public const string AllowedTools = "AllowedTools";

    /// <summary>A <see cref="Domain.Common.Config.AI.Plugins.PluginDeclaration.DeniedTools"/> entry.</summary>
    public const string DeniedTools = "DeniedTools";
}

/// <summary>
/// Extension helpers over a <see cref="PluginToolBoundaryViolation"/> list, used to classify a
/// <c>Faulted</c> boundary's fault shape (#608).
/// </summary>
public static class PluginToolBoundaryViolationExtensions
{
    /// <summary>
    /// Whether a Faulted boundary's <paramref name="violations"/> are provably confined to
    /// <see cref="PluginToolBoundaryListKind.AllowedTools"/> — the one shape (#608) that can never
    /// widen a plugin's access and can never defeat the <see cref="PluginToolBoundaryListKind.DeniedTools"/>
    /// bypass-immune guarantee, so it's safe for a consumer to run its normal boundary filter instead
    /// of denying everything. <see langword="null"/> or an empty list is NOT confined — a registry
    /// that recorded nothing gives no way to rule out a <see cref="PluginToolBoundaryListKind.DeniedTools"/>
    /// hit, so it must be treated the same as a confirmed one: fail closed on uncertainty, not just on
    /// certainty.
    /// </summary>
    /// <remarks>
    /// The leaf check <see cref="RequiresFailClosed"/> is built from — kept separate because it's a
    /// pure fact about a violation list, independent of what a caller does with it.
    /// </remarks>
    public static bool IsConfinedToAllowedTools(this IReadOnlyList<PluginToolBoundaryViolation>? violations) =>
        violations is { Count: > 0 } && violations.All(v => v.ListKind == PluginToolBoundaryListKind.AllowedTools);

    /// <summary>
    /// Whether a plugin's boundary <paramref name="status"/> demands the caller's fail-closed
    /// response (#608) — deny everything, rather than run the normal boundary filter.
    /// <see langword="true"/> for <see cref="PluginBoundaryStatus.Pending"/> unconditionally (it's
    /// transient — resolves to <see cref="PluginBoundaryStatus.Verified"/> or
    /// <see cref="PluginBoundaryStatus.Faulted"/> once every configured MCP server reports, so
    /// narrowing it isn't part of what #608 addressed), and for
    /// <see cref="PluginBoundaryStatus.Faulted"/> unless <paramref name="violations"/> is
    /// <see cref="IsConfinedToAllowedTools">confined to AllowedTools</see>.
    /// </summary>
    /// <remarks>
    /// The single shared decision both <c>ToolChainBuilder.ApplyPluginBoundaryIfPluginSkill</c> and
    /// <c>PluginPermissionRuleProvider.BoundaryDemandsAgentWideFailClosed</c> call, instead of each
    /// independently re-deriving the same Verified/Pending/Faulted branch (#608 code-review /
    /// /simplify — flagged across two review rounds as a divergence risk: only the leaf
    /// <see cref="IsConfinedToAllowedTools"/> check was originally shared, not the branch around it).
    /// <paramref name="violations"/> is only consulted when <paramref name="status"/> is
    /// <see cref="PluginBoundaryStatus.Faulted"/> — callers should pass <see langword="null"/> (or
    /// skip fetching it) otherwise, since <see cref="IPluginRegistry.GetBoundaryViolations"/> is
    /// meaningless for any other status.
    /// </remarks>
    public static bool RequiresFailClosed(
        this PluginBoundaryStatus status, IReadOnlyList<PluginToolBoundaryViolation>? violations) =>
        status switch
        {
            PluginBoundaryStatus.Verified => false,
            PluginBoundaryStatus.Faulted => !violations.IsConfinedToAllowedTools(),
            _ => true, // Pending, or any future status — fail closed on uncertainty.
        };
}

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
/// unmatched entry); everything else is left <see cref="PluginBoundaryStatus.Pending"/> (untrusted,
/// not silently allowed — see that enum's remarks) until <see cref="ReportServerToolsDiscovered"/>
/// resolves it, whether from the harness's own normal MCP use or from
/// <c>PluginToolBoundaryStartupValidator</c> proactively querying <see cref="PendingServerNames"/>
/// right after boot specifically so a server nothing else happens to use doesn't leave a plugin
/// pending — and therefore denied — for the rest of the process lifetime.
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
    /// EVERY MCP server name configured anywhere on the host — enabled AND disabled (#613: a
    /// disabled server is still a real, named server that could explain a boundary entry the moment
    /// it's re-enabled; treating it as "doesn't exist" false-triggers this method's existence check).
    /// <strong>Not</strong> narrowed to any one plugin's own declared servers. A review-round finding
    /// traced <c>ToolChainBuilder.ResolveEffectiveMcpServerName</c> and confirmed a plugin skill's
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

    /// <summary>
    /// Every MCP server name at least one plugin's boundary was <see cref="PluginBoundaryStatus.Pending"/>
    /// on immediately after the most recent <see cref="Seed"/> call. Meant to be read exactly once,
    /// right after <see cref="Seed"/> returns — by <c>PluginToolBoundaryStartupValidator</c>, to decide
    /// which servers to proactively query so pending entries resolve promptly instead of only if the
    /// running session happens to need that server anyway. NOT pruned as entries resolve via
    /// <see cref="ReportServerToolsDiscovered"/> — a server every pending plugin has since resolved
    /// against still appears here — so a caller reading this well after boot gets a superset of what
    /// is genuinely still pending, not a live count.
    /// </summary>
    IReadOnlyCollection<string> PendingServerNames { get; }
}

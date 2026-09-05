using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Plugins;

namespace Application.AI.Common.Services.Plugins;

/// <inheritdoc cref="IPluginToolBoundaryTracker" />
public sealed class PluginToolBoundaryTracker : IPluginToolBoundaryTracker
{
    private const string AllowedToolsListKind = "AllowedTools";
    private const string DeniedToolsListKind = "DeniedTools";

    private sealed class PendingPlugin
    {
        public required object Lock { get; init; }
        public required Dictionary<string, string> PendingEntries { get; init; } // name -> list kind
        public required HashSet<string> PendingServers { get; init; }

        // Guards against firing twice for one plugin: a caller can still hold this same instance
        // (from its own TryGetValue) after _pendingByPlugin.TryRemove has already dropped it — two
        // concurrent ReportServerToolsDiscovered calls for THE SAME server can both reach the lock
        // with PendingServers already empty and PendingEntries never cleared, otherwise faulting
        // (and logging) the same violation twice.
        public bool Resolved { get; set; }
    }

    private readonly IPluginRegistry _registry;
    private readonly ConcurrentDictionary<string, PendingPlugin> _pendingByPlugin = new(StringComparer.OrdinalIgnoreCase);

    // serverName -> plugin names waiting on it, built once at Seed time.
    private readonly ConcurrentDictionary<string, List<string>> _pluginsByServer = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="PluginToolBoundaryTracker"/> class.</summary>
    public PluginToolBoundaryTracker(IPluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginToolBoundaryViolation> Seed(
        IReadOnlyList<LoadedPlugin> loadedPlugins,
        Func<string, bool> isKnownFirstPartyToolName,
        IReadOnlyCollection<string> allConfiguredMcpServerNames)
    {
        ArgumentNullException.ThrowIfNull(loadedPlugins);
        ArgumentNullException.ThrowIfNull(isKnownFirstPartyToolName);
        ArgumentNullException.ThrowIfNull(allConfiguredMcpServerNames);

        var immediate = new List<PluginToolBoundaryViolation>();

        foreach (var plugin in loadedPlugins)
        {
            var entries = BoundaryEntries(plugin);
            // A name can legitimately appear more than once — duplicated within one list, or once
            // in AllowedTools and once in DeniedTools — and the existence question is identical
            // either time, so collapse before anything downstream (the ToDictionary below cannot
            // tolerate a repeated key at all — confirmed by a review round finding it throws
            // ArgumentException on exactly this shape, crashing a legally-configured plugin's boot).
            var unresolved = entries
                .Where(e => !isKnownFirstPartyToolName(e.Name))
                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (unresolved.Count == 0)
                continue;

            if (allConfiguredMcpServerNames.Count == 0)
            {
                // No MCP server exists ANYWHERE on this host — no other source these could ever
                // resolve from, provably fake right now. Deliberately NOT scoped to plugin.McpServerNames
                // (the plugin's own declared servers): a review round finding traced
                // ToolChainBuilder.ResolveEffectiveMcpServerName and confirmed a plugin skill's
                // ToolDeclaration can resolve against ANY host-configured MCP server, not only ones
                // the plugin itself declares — a zero-own-servers plugin can still legitimately
                // reference a host-level server's tool in its boundary. Narrowing to the plugin's own
                // servers previously crashed boot (or permanently denied every tool) on exactly that
                // valid, pre-existing configuration.
                immediate.AddRange(unresolved.Select(e => new PluginToolBoundaryViolation(plugin.Name, e.ListKind, e.Name)));
                continue;
            }

            var pending = new PendingPlugin
            {
                Lock = new object(),
                PendingEntries = unresolved.ToDictionary(e => e.Name, e => e.ListKind, StringComparer.OrdinalIgnoreCase),
                PendingServers = new HashSet<string>(allConfiguredMcpServerNames, StringComparer.OrdinalIgnoreCase),
            };
            _pendingByPlugin[plugin.Name] = pending;

            foreach (var serverName in allConfiguredMcpServerNames)
                _pluginsByServer.GetOrAdd(serverName, _ => []).Add(plugin.Name);
        }

        return immediate;
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginToolBoundaryViolation> ReportServerToolsDiscovered(
        string serverName, IReadOnlyCollection<string> discoveredToolNames)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        ArgumentNullException.ThrowIfNull(discoveredToolNames);

        if (!_pluginsByServer.TryGetValue(serverName, out var pluginNames))
            return [];

        var discovered = new HashSet<string>(discoveredToolNames, StringComparer.OrdinalIgnoreCase);
        var violations = new List<PluginToolBoundaryViolation>();

        foreach (var pluginName in pluginNames)
        {
            if (!_pendingByPlugin.TryGetValue(pluginName, out var pending))
                continue; // Already resolved and removed by a concurrent report.

            List<PluginToolBoundaryViolation>? faulted = null;
            lock (pending.Lock)
            {
                // A concurrent report for a DIFFERENT server on this same plugin can already have
                // resolved it before this thread reached the lock — this thread's own TryGetValue
                // above ran against a reference that predates that removal, so it must re-check here.
                if (pending.Resolved)
                    continue;

                foreach (var name in pending.PendingEntries.Keys.Where(discovered.Contains).ToList())
                    pending.PendingEntries.Remove(name);

                pending.PendingServers.Remove(serverName);

                if (pending.PendingServers.Count > 0)
                    continue; // Other servers this plugin depends on haven't reported yet.

                // Last pending server just reported. Whatever's still unresolved is now provably fake.
                if (pending.PendingEntries.Count > 0)
                {
                    faulted = pending.PendingEntries
                        .Select(kv => new PluginToolBoundaryViolation(pluginName, kv.Value, kv.Key))
                        .ToList();
                }

                pending.Resolved = true;
                _pendingByPlugin.TryRemove(pluginName, out _);
            }

            if (faulted is { Count: > 0 })
            {
                _registry.MarkBoundaryFaulted(
                    pluginName,
                    $"Tool boundary entries match no known tool: {string.Join(", ", faulted.Select(v => $"{v.ListKind}:{v.ToolName}"))}");
                violations.AddRange(faulted);
            }
        }

        return violations;
    }

    private static IReadOnlyList<(string Name, string ListKind)> BoundaryEntries(LoadedPlugin plugin)
    {
        var entries = new List<(string, string)>();
        if (plugin.Declaration.AllowedTools is { Count: > 0 } allowed)
            entries.AddRange(allowed.Select(name => (name, AllowedToolsListKind)));
        if (plugin.Declaration.DeniedTools is { Count: > 0 } denied)
            entries.AddRange(denied.Select(name => (name, DeniedToolsListKind)));
        return entries;
    }
}

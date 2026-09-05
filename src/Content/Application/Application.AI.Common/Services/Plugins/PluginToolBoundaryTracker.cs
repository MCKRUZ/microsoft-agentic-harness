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
        IReadOnlyList<LoadedPlugin> loadedPlugins, Func<string, bool> isKnownFirstPartyToolName)
    {
        ArgumentNullException.ThrowIfNull(loadedPlugins);
        ArgumentNullException.ThrowIfNull(isKnownFirstPartyToolName);

        var immediate = new List<PluginToolBoundaryViolation>();

        foreach (var plugin in loadedPlugins)
        {
            var entries = BoundaryEntries(plugin);
            var unresolved = entries.Where(e => !isKnownFirstPartyToolName(e.Name)).ToList();
            if (unresolved.Count == 0)
                continue;

            if (plugin.McpServerNames.Count == 0)
            {
                // No other source these could ever resolve from — provably fake, right now.
                immediate.AddRange(unresolved.Select(e => new PluginToolBoundaryViolation(plugin.Name, e.ListKind, e.Name)));
                continue;
            }

            var pending = new PendingPlugin
            {
                Lock = new object(),
                PendingEntries = unresolved.ToDictionary(e => e.Name, e => e.ListKind, StringComparer.OrdinalIgnoreCase),
                PendingServers = new HashSet<string>(plugin.McpServerNames, StringComparer.OrdinalIgnoreCase),
            };
            _pendingByPlugin[plugin.Name] = pending;

            foreach (var serverName in plugin.McpServerNames)
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

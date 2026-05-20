using System.Diagnostics;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.GraphRag;

/// <summary>
/// Applies time-based exponential decay to cross-session memory nodes stored in the
/// knowledge graph, and prunes those whose weight falls below a configurable threshold.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Decay formula:</strong>
/// <code>
/// newWeight = currentWeight * Math.Pow(1 - decayRate, daysSinceLastAccess)
/// </code>
/// where <c>decayRate</c> is configured via <see cref="Domain.Common.Config.AI.RAG.CrossSessionMemoryConfig.DecayRate"/>
/// (default 0.05 per day) and <c>daysSinceLastAccess</c> is clamped to ≥ 0 to guard
/// against future-dated timestamps.
/// </para>
/// <para>
/// Memory nodes are identified by <c>Type == "Memory"</c> in the graph backend. Each node
/// stores its current weight as <c>Properties["weight"]</c> (F4 format, e.g. "0.8000") and
/// its last access timestamp as <c>Properties["last_accessed_at"]</c> (ISO 8601).
/// </para>
/// <para>
/// Updates with a change smaller than 0.0001 are skipped to avoid unnecessary write
/// amplification on recently accessed memories.
/// </para>
/// <para>
/// This class is <c>IDisposable</c> to allow tests to clean up its <see cref="ActivitySource"/>.
/// In production it is registered as a singleton and disposed by the DI container.
/// </para>
/// </remarks>
public sealed class MemoryDecayService : IMemoryDecayService, IDisposable
{
    private static readonly ActivitySource _activitySource = new("Infrastructure.AI.RAG.GraphRag");

    private readonly IGraphDatabaseBackend _graphBackend;
    private readonly ICrossSessionMemoryStore _memoryStore;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly ILogger<MemoryDecayService> _logger;

    /// <summary>
    /// Initialises a new <see cref="MemoryDecayService"/> with the required dependencies.
    /// </summary>
    /// <param name="graphBackend">Graph backend used to read, update, and delete memory nodes.</param>
    /// <param name="memoryStore">Cross-session memory store for cascading deletions via <c>ForgetAsync</c>.</param>
    /// <param name="configMonitor">Live configuration providing decay rate and prune threshold.</param>
    /// <param name="logger">Logger for operational metrics (nodes updated, nodes pruned).</param>
    public MemoryDecayService(
        IGraphDatabaseBackend graphBackend,
        ICrossSessionMemoryStore memoryStore,
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<MemoryDecayService> logger)
    {
        _graphBackend = graphBackend;
        _memoryStore = memoryStore;
        _configMonitor = configMonitor;
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Iterates all nodes of <c>Type == "Memory"</c>, computes the elapsed days since
    /// <c>Properties["last_accessed_at"]</c>, and applies the EMA decay formula. Nodes
    /// whose weight changes by less than 0.0001 are skipped. Updated weights are written
    /// back via <see cref="IKnowledgeGraphStore.AddNodesAsync"/> (upsert semantics).
    /// </remarks>
    public async Task ApplyDecayAsync(CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity(nameof(ApplyDecayAsync));

        var decayRate = _configMonitor.CurrentValue.AI.Rag.CrossSessionMemory.DecayRate;
        var now = DateTimeOffset.UtcNow;
        var allNodes = await _graphBackend.GetAllNodesAsync(cancellationToken);
        var memoryNodes = allNodes.Where(n => n.Type == "Memory").ToList();

        var updatedNodes = new List<Domain.AI.KnowledgeGraph.Models.GraphNode>();

        foreach (var node in memoryNodes)
        {
            if (!node.Properties.TryGetValue("weight", out var weightStr) ||
                !double.TryParse(weightStr, out var currentWeight))
                continue;

            if (!node.Properties.TryGetValue("last_accessed_at", out var lastAccessedStr) ||
                !DateTimeOffset.TryParse(lastAccessedStr, out var lastAccessed))
                continue;

            var daysSinceAccess = Math.Max(0.0, (now - lastAccessed).TotalDays);
            var newWeight = currentWeight * Math.Pow(1.0 - decayRate, daysSinceAccess);

            if (Math.Abs(newWeight - currentWeight) < 0.0001)
                continue;

            var updatedProperties = new Dictionary<string, string>(node.Properties)
            {
                ["weight"] = newWeight.ToString("F4")
            };

            updatedNodes.Add(node with { Properties = updatedProperties });
        }

        if (updatedNodes.Count > 0)
            await _graphBackend.AddNodesAsync(updatedNodes, cancellationToken);

        _logger.LogInformation(
            "ApplyDecayAsync: evaluated {Total} memory nodes, updated weights on {Updated}.",
            memoryNodes.Count,
            updatedNodes.Count);

        activity?.SetTag("memory.nodes.evaluated", memoryNodes.Count);
        activity?.SetTag("memory.nodes.updated", updatedNodes.Count);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Iterates all nodes of <c>Type == "Memory"</c> and deletes those whose
    /// <c>Properties["weight"]</c> is strictly below <paramref name="threshold"/>.
    /// Each deletion cascades: <see cref="IGraphDatabaseBackend.DeleteNodeAsync"/> removes
    /// the graph node, then <see cref="ICrossSessionMemoryStore.ForgetAsync"/> purges the
    /// corresponding in-memory cache and backing store entry.
    /// </remarks>
    public async Task PruneAsync(double threshold, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity(nameof(PruneAsync));
        activity?.SetTag("memory.prune.threshold", threshold);

        var allNodes = await _graphBackend.GetAllNodesAsync(cancellationToken);
        var memoryNodes = allNodes.Where(n => n.Type == "Memory").ToList();

        var pruned = 0;

        foreach (var node in memoryNodes)
        {
            if (!node.Properties.TryGetValue("weight", out var weightStr) ||
                !double.TryParse(weightStr, out var weight))
                continue;

            if (weight >= threshold)
                continue;

            await _graphBackend.DeleteNodeAsync(node.Id, cancellationToken);
            await _memoryStore.ForgetAsync(node.Id, cancellationToken);
            pruned++;
        }

        _logger.LogInformation(
            "PruneAsync(threshold={Threshold}): evaluated {Total} memory nodes, pruned {Pruned}.",
            threshold,
            memoryNodes.Count,
            pruned);

        activity?.SetTag("memory.nodes.evaluated", memoryNodes.Count);
        activity?.SetTag("memory.nodes.pruned", pruned);
    }

    /// <inheritdoc/>
    public void Dispose() => _activitySource.Dispose();
}

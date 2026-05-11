using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.DriftDetection;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.DriftDetection;

/// <summary>
/// Graph-backed persistence for drift baselines. Uses <see cref="IKnowledgeGraphStore"/>
/// with deterministic node IDs (<c>"driftbaseline:{scope}:{identifier}"</c>) for O(1) lookups.
/// </summary>
/// <remarks>
/// <para>
/// Each baseline is stored as a <see cref="GraphNode"/> with <c>Type = "DriftBaseline"</c>.
/// Complex properties (<see cref="DriftBaseline.Dimensions"/>, <see cref="DriftBaseline.DimensionSigmas"/>)
/// are JSON-serialized into the node's <see cref="GraphNode.Properties"/> dictionary.
/// </para>
/// <para>
/// A <c>"baseline_for"</c> edge connects each baseline node to a scope identifier node,
/// enabling graph traversal queries that discover all baselines for a given scope entity.
/// </para>
/// </remarks>
public sealed class GraphDriftBaselineStore : IDriftBaselineStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IKnowledgeGraphStore _graphStore;
    private readonly ILogger<GraphDriftBaselineStore> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="GraphDriftBaselineStore"/>.
    /// </summary>
    /// <param name="graphStore">The knowledge graph backend for node/edge persistence.</param>
    /// <param name="logger">Logger for error diagnostics.</param>
    public GraphDriftBaselineStore(
        IKnowledgeGraphStore graphStore,
        ILogger<GraphDriftBaselineStore> logger)
    {
        _graphStore = graphStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> SaveBaselineAsync(DriftBaseline baseline, CancellationToken ct)
    {
        var nodeId = BuildId(baseline.Scope, baseline.ScopeIdentifier);

        try
        {
            var node = SerializeBaseline(baseline, nodeId);
            await _graphStore.AddNodesAsync([node], ct);

            var scopeNodeId = $"scope:{baseline.Scope.ToString().ToLowerInvariant()}:{baseline.ScopeIdentifier.ToLowerInvariant()}";
            var scopeNode = new GraphNode
            {
                Id = scopeNodeId,
                Name = $"{baseline.Scope}:{baseline.ScopeIdentifier}",
                Type = "ScopeIdentifier"
            };
            await _graphStore.AddNodesAsync([scopeNode], ct);

            var edge = new GraphEdge
            {
                Id = $"{nodeId}->baseline_for->{scopeNodeId}",
                SourceNodeId = nodeId,
                TargetNodeId = scopeNodeId,
                Predicate = "baseline_for",
                ChunkId = nodeId
            };
            await _graphStore.AddEdgesAsync([edge], ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save drift baseline for {Id}", nodeId);
            return Result.Fail($"Failed to save drift baseline: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<DriftBaseline?>> GetBaselineAsync(
        DriftScope scope, string scopeIdentifier, CancellationToken ct)
    {
        var nodeId = BuildId(scope, scopeIdentifier);

        try
        {
            var node = await _graphStore.GetNodeAsync(nodeId, ct);
            if (node is null)
                return Result<DriftBaseline?>.Success(null);

            var baseline = DeserializeBaseline(node);
            return Result<DriftBaseline?>.Success(baseline);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get drift baseline for {Id}", nodeId);
            return Result<DriftBaseline?>.Fail($"Failed to retrieve drift baseline: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<DriftBaseline>>> GetBaselinesAsync(
        DriftScope? scope, CancellationToken ct)
    {
        try
        {
            var allNodes = await _graphStore.GetAllNodesAsync(ct);

            var baselines = allNodes
                .Where(n => n.Type == "DriftBaseline")
                .Where(n => scope is null || n.Properties.TryGetValue("Scope", out var s) && Enum.TryParse<DriftScope>(s, out var parsed) && parsed == scope)
                .Select(DeserializeBaseline)
                .ToList();

            return Result<IReadOnlyList<DriftBaseline>>.Success(baselines.AsReadOnly());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get drift baselines for scope {Scope}", scope);
            return Result<IReadOnlyList<DriftBaseline>>.Fail($"Failed to retrieve drift baselines: {ex.Message}");
        }
    }

    private static string BuildId(DriftScope scope, string scopeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeIdentifier);
        if (scopeIdentifier.Contains(':'))
            throw new ArgumentException("ScopeIdentifier must not contain colons.", nameof(scopeIdentifier));

        return $"driftbaseline:{scope.ToString().ToLowerInvariant()}:{scopeIdentifier.ToLowerInvariant()}";
    }

    private static GraphNode SerializeBaseline(DriftBaseline baseline, string nodeId) => new()
    {
        Id = nodeId,
        Name = $"DriftBaseline:{baseline.Scope}:{baseline.ScopeIdentifier}",
        Type = "DriftBaseline",
        Properties = new Dictionary<string, string>
        {
            ["BaselineId"] = baseline.BaselineId.ToString(),
            ["Scope"] = baseline.Scope.ToString(),
            ["ScopeIdentifier"] = baseline.ScopeIdentifier,
            ["Dimensions"] = JsonSerializer.Serialize(baseline.Dimensions, s_jsonOptions),
            ["DimensionSigmas"] = JsonSerializer.Serialize(baseline.DimensionSigmas, s_jsonOptions),
            ["SampleCount"] = baseline.SampleCount.ToString(CultureInfo.InvariantCulture),
            ["WindowStart"] = baseline.WindowStart.ToString("o"),
            ["WindowEnd"] = baseline.WindowEnd.ToString("o"),
            ["CreatedAt"] = baseline.CreatedAt.ToString("o")
        }.AsReadOnly()
    };

    private static DriftBaseline DeserializeBaseline(GraphNode node) => new()
    {
        BaselineId = Guid.Parse(node.Properties["BaselineId"]),
        Scope = Enum.Parse<DriftScope>(node.Properties["Scope"]),
        ScopeIdentifier = node.Properties["ScopeIdentifier"],
        Dimensions = JsonSerializer.Deserialize<Dictionary<DriftDimension, double>>(
            node.Properties["Dimensions"], s_jsonOptions)!.AsReadOnly(),
        DimensionSigmas = JsonSerializer.Deserialize<Dictionary<DriftDimension, double>>(
            node.Properties["DimensionSigmas"], s_jsonOptions)!.AsReadOnly(),
        SampleCount = int.Parse(node.Properties["SampleCount"], CultureInfo.InvariantCulture),
        WindowStart = DateTimeOffset.Parse(node.Properties["WindowStart"], CultureInfo.InvariantCulture),
        WindowEnd = DateTimeOffset.Parse(node.Properties["WindowEnd"], CultureInfo.InvariantCulture),
        CreatedAt = DateTimeOffset.Parse(node.Properties["CreatedAt"], CultureInfo.InvariantCulture)
    };
}

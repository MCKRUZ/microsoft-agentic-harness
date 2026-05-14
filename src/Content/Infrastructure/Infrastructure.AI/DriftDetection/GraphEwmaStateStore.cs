using System.Globalization;
using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.DriftDetection;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.DriftDetection;

/// <summary>
/// Persists EWMA state as knowledge graph nodes with deterministic IDs for O(1) lookup.
/// ID format: "ewma:{scope}:{identifier}:{dimension}"
/// </summary>
public sealed class GraphEwmaStateStore : IEwmaStateStore
{
    private readonly IKnowledgeGraphStore _graphStore;
    private readonly ILogger<GraphEwmaStateStore> _logger;

    public GraphEwmaStateStore(
        IKnowledgeGraphStore graphStore,
        ILogger<GraphEwmaStateStore> logger)
    {
        _graphStore = graphStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<EwmaState?>> GetStateAsync(
        DriftScope scope, string scopeIdentifier, DriftDimension dimension, CancellationToken ct)
    {
        var id = BuildId(scope, scopeIdentifier, dimension);

        try
        {
            var node = await _graphStore.GetNodeAsync(id, ct);
            if (node is null)
                return Result<EwmaState?>.Success(null);

            var state = DeserializeState(node);
            return Result<EwmaState?>.Success(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get EWMA state for {Id}", id);
            return Result<EwmaState?>.Fail($"Failed to retrieve EWMA state: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result> SaveStateAsync(EwmaState state, CancellationToken ct)
    {
        var node = SerializeState(state);

        try
        {
            await _graphStore.AddNodesAsync([node], ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save EWMA state for {Id}", state.DeterministicId);
            return Result.Fail($"Failed to save EWMA state: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<EwmaState>>> GetStatesAsync(
        DriftScope scope, string scopeIdentifier, CancellationToken ct)
    {
        try
        {
            var dimensions = Enum.GetValues<DriftDimension>();
            var tasks = dimensions.Select(d =>
            {
                var id = BuildId(scope, scopeIdentifier, d);
                return _graphStore.GetNodeAsync(id, ct);
            });

            var nodes = await Task.WhenAll(tasks);
            var states = nodes
                .Where(n => n is not null)
                .Select(n => DeserializeState(n!))
                .ToList();

            return Result<IReadOnlyList<EwmaState>>.Success(states.AsReadOnly());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get EWMA states for {Scope}:{Identifier}", scope, scopeIdentifier);
            return Result<IReadOnlyList<EwmaState>>.Fail($"Failed to retrieve EWMA states: {ex.Message}");
        }
    }

    private static string BuildId(DriftScope scope, string scopeIdentifier, DriftDimension dimension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeIdentifier);
        if (scopeIdentifier.Contains(':'))
            throw new ArgumentException("ScopeIdentifier must not contain colons.", nameof(scopeIdentifier));

        return $"ewma:{scope}:{scopeIdentifier}:{dimension}";
    }

    private static GraphNode SerializeState(EwmaState state) => new()
    {
        Id = state.DeterministicId,
        Name = state.DeterministicId,
        Type = "EwmaState",
        Properties = new Dictionary<string, string>
        {
            ["Scope"] = state.Scope.ToString(),
            ["ScopeIdentifier"] = state.ScopeIdentifier,
            ["Dimension"] = state.Dimension.ToString(),
            ["CurrentEwma"] = state.CurrentEwma.ToString(CultureInfo.InvariantCulture),
            ["SampleCount"] = state.SampleCount.ToString(CultureInfo.InvariantCulture),
            ["LastUpdatedAt"] = state.LastUpdatedAt.ToString("o")
        }.AsReadOnly()
    };

    private static EwmaState DeserializeState(GraphNode node) => new()
    {
        Scope = Enum.Parse<DriftScope>(node.Properties["Scope"]),
        ScopeIdentifier = node.Properties["ScopeIdentifier"],
        Dimension = Enum.Parse<DriftDimension>(node.Properties["Dimension"]),
        CurrentEwma = double.Parse(node.Properties["CurrentEwma"], CultureInfo.InvariantCulture),
        SampleCount = int.Parse(node.Properties["SampleCount"], CultureInfo.InvariantCulture),
        LastUpdatedAt = DateTimeOffset.Parse(node.Properties["LastUpdatedAt"], CultureInfo.InvariantCulture)
    };
}

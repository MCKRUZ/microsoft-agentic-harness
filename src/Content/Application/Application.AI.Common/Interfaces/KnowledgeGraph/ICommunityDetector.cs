using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Detects communities of related entities in the knowledge graph using a graph
/// partitioning algorithm (e.g., Leiden). Produces hierarchical communities at multiple levels.
/// </summary>
public interface ICommunityDetector
{
    /// <summary>Detects communities at multiple hierarchy levels. Level 0 is most granular.</summary>
    Task<IReadOnlyList<Community>> DetectAsync(IGraphDatabaseBackend graph, int targetLevels, CancellationToken cancellationToken = default);
}

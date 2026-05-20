using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Extended graph storage abstraction adding community detection support, node weight
/// updates, and deep traversal on top of the base <see cref="IKnowledgeGraphStore"/> operations.
/// </summary>
public interface IGraphDatabaseBackend : IKnowledgeGraphStore
{
    /// <summary>Retrieves all nodes assigned to a specific community.</summary>
    Task<IReadOnlyList<GraphNode>> GetCommunityNodesAsync(string communityId, CancellationToken cancellationToken = default);

    /// <summary>Retrieves all detected communities at the specified hierarchy level.</summary>
    Task<IReadOnlyList<Community>> GetCommunitiesAsync(int level, CancellationToken cancellationToken = default);

    /// <summary>Assigns a node to a community at the specified hierarchy level.</summary>
    Task AssignCommunityAsync(string nodeId, string communityId, int level, CancellationToken cancellationToken = default);

    /// <summary>Updates the feedback weight of a node.</summary>
    Task UpdateNodeWeightAsync(string nodeId, double weight, CancellationToken cancellationToken = default);

    /// <summary>Traverses the graph from a node up to maxDepth hops via breadth-first search.</summary>
    Task<IReadOnlyList<GraphNode>> TraverseAsync(string startNodeId, int maxDepth, CancellationToken cancellationToken = default);

    /// <summary>Persists a community record (including summary) to the graph backend.</summary>
    Task SaveCommunityAsync(Community community, CancellationToken cancellationToken = default);
}

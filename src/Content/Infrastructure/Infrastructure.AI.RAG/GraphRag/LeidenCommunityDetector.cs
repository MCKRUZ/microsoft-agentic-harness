using System.Diagnostics;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.GraphRag;

/// <summary>
/// Simplified Leiden-inspired community detection algorithm for knowledge graph partitioning.
/// Produces hierarchical communities at multiple resolution levels suitable for GraphRAG
/// global search, where each community gets a summarized representation of its member nodes.
/// </summary>
/// <remarks>
/// <para>
/// This implementation uses a two-phase approach at each level:
/// <list type="number">
///   <item><term>Initialization</term><description>
///     Nodes are seeded into communities via BFS connected-component discovery.
///   </description></item>
///   <item><term>Modularity Optimization</term><description>
///     Iteratively moves nodes to the neighbor community that yields the highest modularity
///     gain, for up to 50 iterations or until no improvement is found.
///   </description></item>
///   <item><term>Refinement</term><description>
///     Splits any community that became internally disconnected during optimization.
///   </description></item>
///   <item><term>Hierarchy</term><description>
///     For levels above 0, communities from the previous level become super-nodes and the
///     process repeats, producing a coarser partition.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// Modularity gain formula: <c>(edgesToTarget / totalEdges) - (ki * communityDegree / (2 * totalEdges^2))</c>
/// where <c>ki</c> is the degree of the moving node and <c>communityDegree</c> is the
/// sum of degrees of all nodes in the target community.
/// </para>
/// </remarks>
public sealed class LeidenCommunityDetector : ICommunityDetector
{
    private static readonly ActivitySource _activitySource =
        new("Infrastructure.AI.RAG.GraphRag");

    private const int MaxIterations = 50;

    private readonly ILogger<LeidenCommunityDetector> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="LeidenCommunityDetector"/>.
    /// </summary>
    /// <param name="logger">Logger for recording community detection progress.</param>
    public LeidenCommunityDetector(ILogger<LeidenCommunityDetector> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Community>> DetectAsync(
        IGraphDatabaseBackend graph,
        int targetLevels,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ArgumentNullException.ThrowIfNull(graph);

        using var activity = _activitySource.StartActivity("LeidenCommunityDetector.DetectAsync");

        var allNodes = await graph.GetAllNodesAsync(cancellationToken).ConfigureAwait(false);
        if (allNodes.Count == 0)
        {
            _logger.LogDebug("LeidenCommunityDetector: empty graph, returning no communities");
            return [];
        }

        var allTriplets = await graph.GetTripletsAsync([], cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "LeidenCommunityDetector: {NodeCount} nodes, {EdgeCount} edges, {TargetLevels} levels",
            allNodes.Count, allTriplets.Count, targetLevels);

        var result = new List<Community>();

        // Working set for the current level: node IDs within each super-node
        // At level 0, each super-node is a single real node.
        var superNodes = allNodes
            .ToDictionary(n => n.Id, n => (IReadOnlyList<string>)[n.Id]);

        // Adjacency at the super-node level
        var adjacency = BuildAdjacency(allTriplets.Select(t => (t.Edge.SourceNodeId, t.Edge.TargetNodeId)));

        for (var level = 0; level < targetLevels; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nodeIds = superNodes.Keys.ToList();

            // Phase 1: seed communities via BFS connected components
            var assignment = SeedByCc(nodeIds, adjacency);

            // Phase 2: modularity optimization
            OptimizeModularity(nodeIds, adjacency, assignment);

            // Phase 3: connectivity refinement (split disconnected communities)
            RefineConnectivity(nodeIds, adjacency, assignment);

            // Build Community records for this level
            var levelCommunities = BuildCommunityRecords(level, assignment, superNodes);

            await PersistCommunitiesAsync(graph, levelCommunities, cancellationToken)
                .ConfigureAwait(false);

            result.AddRange(levelCommunities);

            _logger.LogDebug(
                "LeidenCommunityDetector: level {Level} produced {Count} communities",
                level, levelCommunities.Count);

            if (level + 1 >= targetLevels)
                break;

            // Build super-nodes for the next level:
            // each community becomes a super-node containing all real node IDs of its members.
            var nextSuperNodes = new Dictionary<string, IReadOnlyList<string>>();
            var nextAdjacency = new Dictionary<string, HashSet<string>>();

            foreach (var community in levelCommunities)
            {
                // The community's real-node members are the union of all current super-node members
                var realNodeIds = community.NodeIds
                    .SelectMany(snId => superNodes.TryGetValue(snId, out var members) ? members : [snId])
                    .Distinct()
                    .ToList();

                nextSuperNodes[community.Id] = realNodeIds;
                nextAdjacency[community.Id] = [];
            }

            // Wire adjacency between super-nodes based on cross-community edges in the current level
            var superNodeByCommunityMember = new Dictionary<string, string>();
            foreach (var (snId, _) in superNodes)
            {
                var comm = levelCommunities.FirstOrDefault(c => c.NodeIds.Contains(snId));
                if (comm is not null)
                    superNodeByCommunityMember[snId] = comm.Id;
            }

            foreach (var (src, targets) in adjacency)
            {
                if (!superNodeByCommunityMember.TryGetValue(src, out var srcComm))
                    continue;
                foreach (var tgt in targets)
                {
                    if (!superNodeByCommunityMember.TryGetValue(tgt, out var tgtComm))
                        continue;
                    if (srcComm == tgtComm)
                        continue;

                    if (!nextAdjacency.ContainsKey(srcComm))
                        nextAdjacency[srcComm] = [];
                    if (!nextAdjacency.ContainsKey(tgtComm))
                        nextAdjacency[tgtComm] = [];

                    nextAdjacency[srcComm].Add(tgtComm);
                    nextAdjacency[tgtComm].Add(srcComm);
                }
            }

            superNodes = nextSuperNodes;
            adjacency = nextAdjacency.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlySet<string>)kvp.Value);
        }

        activity?.SetTag(RagConventions.GraphCommunityLevel, targetLevels);
        activity?.SetTag("rag.graph.community_count", result.Count);

        return result;
    }

    // ── Private static helpers ────────────────────────────────────────────────

    /// <summary>
    /// Builds an undirected adjacency map from directed edge pairs.
    /// Both directions are added so the graph is treated as undirected during community detection.
    /// </summary>
    private static Dictionary<string, IReadOnlySet<string>> BuildAdjacency(
        IEnumerable<(string Src, string Tgt)> edges)
    {
        var adj = new Dictionary<string, HashSet<string>>();

        foreach (var (src, tgt) in edges)
        {
            if (!adj.TryGetValue(src, out var srcSet))
                adj[src] = srcSet = [];
            if (!adj.TryGetValue(tgt, out var tgtSet))
                adj[tgt] = tgtSet = [];

            srcSet.Add(tgt);
            tgtSet.Add(src);
        }

        return adj.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlySet<string>)kvp.Value);
    }

    /// <summary>
    /// Seeds community assignments via BFS connected-component discovery.
    /// Each connected component starts as its own community.
    /// </summary>
    private static Dictionary<string, int> SeedByCc(
        IReadOnlyList<string> nodeIds,
        IReadOnlyDictionary<string, IReadOnlySet<string>> adjacency)
    {
        var assignment = new Dictionary<string, int>();
        var communityId = 0;

        foreach (var nodeId in nodeIds)
        {
            if (assignment.ContainsKey(nodeId))
                continue;

            // BFS from this unvisited node
            var queue = new Queue<string>();
            queue.Enqueue(nodeId);
            assignment[nodeId] = communityId;

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!adjacency.TryGetValue(current, out var neighbors))
                    continue;

                foreach (var neighbor in neighbors)
                {
                    if (!assignment.ContainsKey(neighbor) && nodeIds.Contains(neighbor))
                    {
                        assignment[neighbor] = communityId;
                        queue.Enqueue(neighbor);
                    }
                }
            }

            communityId++;
        }

        // Ensure all nodes have an assignment (isolated nodes not in adjacency)
        foreach (var nodeId in nodeIds)
        {
            if (!assignment.ContainsKey(nodeId))
                assignment[nodeId] = communityId++;
        }

        return assignment;
    }

    /// <summary>
    /// Iteratively moves nodes to the neighbor community that yields the highest positive
    /// modularity gain. Runs for at most <see cref="MaxIterations"/> passes or until stable.
    /// </summary>
    private static void OptimizeModularity(
        IReadOnlyList<string> nodeIds,
        IReadOnlyDictionary<string, IReadOnlySet<string>> adjacency,
        Dictionary<string, int> assignment)
    {
        var totalEdges = adjacency.Values.Sum(s => s.Count) / 2.0;
        if (totalEdges <= 0)
            return;

        for (var iter = 0; iter < MaxIterations; iter++)
        {
            var improved = false;

            foreach (var nodeId in nodeIds)
            {
                if (!adjacency.TryGetValue(nodeId, out var neighbors) || neighbors.Count == 0)
                    continue;

                var currentComm = assignment[nodeId];

                // Degree of this node
                var ki = (double)neighbors.Count;

                // Count edges from this node to each neighboring community
                var edgesToCommunity = new Dictionary<int, double>();
                foreach (var neighbor in neighbors)
                {
                    if (!assignment.TryGetValue(neighbor, out var neighborComm))
                        continue;
                    if (!edgesToCommunity.ContainsKey(neighborComm))
                        edgesToCommunity[neighborComm] = 0;
                    edgesToCommunity[neighborComm]++;
                }

                // Community degree sums
                var communityDegree = new Dictionary<int, double>();
                foreach (var nId in nodeIds)
                {
                    var comm = assignment[nId];
                    var degree = adjacency.TryGetValue(nId, out var nNeighbors)
                        ? (double)nNeighbors.Count : 0;
                    if (!communityDegree.ContainsKey(comm))
                        communityDegree[comm] = 0;
                    communityDegree[comm] += degree;
                }

                // Find best community among neighbors (excluding current)
                var bestComm = currentComm;
                var bestGain = 0.0;

                foreach (var (candidateComm, edgesToTarget) in edgesToCommunity)
                {
                    if (candidateComm == currentComm)
                        continue;

                    var targetDegree = communityDegree.TryGetValue(candidateComm, out var d) ? d : 0;
                    var gain = (edgesToTarget / totalEdges)
                        - (ki * targetDegree / (2.0 * totalEdges * totalEdges));

                    if (gain > bestGain)
                    {
                        bestGain = gain;
                        bestComm = candidateComm;
                    }
                }

                if (bestComm != currentComm)
                {
                    assignment[nodeId] = bestComm;
                    improved = true;
                }
            }

            if (!improved)
                break;
        }
    }

    /// <summary>
    /// Splits any community that became internally disconnected during optimization.
    /// Uses BFS within each community's induced subgraph to find connected components.
    /// </summary>
    private static void RefineConnectivity(
        IReadOnlyList<string> nodeIds,
        IReadOnlyDictionary<string, IReadOnlySet<string>> adjacency,
        Dictionary<string, int> assignment)
    {
        var communityGroups = nodeIds
            .GroupBy(n => assignment[n])
            .ToDictionary(g => g.Key, g => g.ToHashSet());

        var nextCommId = assignment.Values.DefaultIfEmpty(0).Max() + 1;

        foreach (var (commId, members) in communityGroups)
        {
            if (members.Count <= 1)
                continue;

            // BFS within this community's induced subgraph
            var visited = new HashSet<string>();

            foreach (var seed in members)
            {
                if (visited.Contains(seed))
                    continue;

                var queue = new Queue<string>();
                queue.Enqueue(seed);
                visited.Add(seed);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (!adjacency.TryGetValue(current, out var neighbors))
                        continue;

                    foreach (var neighbor in neighbors)
                    {
                        if (!visited.Contains(neighbor) && members.Contains(neighbor))
                        {
                            visited.Add(neighbor);
                            queue.Enqueue(neighbor);
                        }
                    }
                }

                // Any members not yet visited form a new component — assign new community ID
                // (first component keeps original commId, subsequent ones get new IDs)
                if (visited.Count < members.Count)
                {
                    foreach (var member in members)
                    {
                        if (!visited.Contains(member))
                            assignment[member] = nextCommId;
                    }

                    nextCommId++;
                    break; // re-run on next iteration if needed; one split per pass is enough
                }
            }
        }
    }

    /// <summary>
    /// Converts the integer community assignments into <see cref="Community"/> records
    /// using the provided super-node map to expand members into real node IDs.
    /// </summary>
    private static List<Community> BuildCommunityRecords(
        int level,
        Dictionary<string, int> assignment,
        Dictionary<string, IReadOnlyList<string>> superNodes)
    {
        var grouped = assignment
            .GroupBy(kvp => kvp.Value, kvp => kvp.Key)
            .ToList();

        var communities = new List<Community>(grouped.Count);
        var index = 0;

        foreach (var group in grouped)
        {
            var superNodeIds = group.ToList();

            // Expand super-node IDs to real node IDs
            var realNodeIds = superNodeIds
                .SelectMany(snId => superNodes.TryGetValue(snId, out var members) ? members : [snId])
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            communities.Add(new Community
            {
                Id = $"community_{level}_{index}",
                Level = level,
                Summary = string.Empty, // LLM summarization is a separate pipeline step
                NodeIds = realNodeIds,
                Modularity = 0.0,       // Computed post-optimization; placeholder for now
            });

            index++;
        }

        return communities;
    }

    /// <summary>
    /// Persists all community records and their node assignments to the graph backend.
    /// </summary>
    private static async Task PersistCommunitiesAsync(
        IGraphDatabaseBackend graph,
        IReadOnlyList<Community> communities,
        CancellationToken cancellationToken)
    {
        foreach (var community in communities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await graph.SaveCommunityAsync(community, cancellationToken).ConfigureAwait(false);

            foreach (var nodeId in community.NodeIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await graph.AssignCommunityAsync(nodeId, community.Id, community.Level, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}

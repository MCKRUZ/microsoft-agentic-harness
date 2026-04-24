using System.Diagnostics;
using System.Text.Json;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace Infrastructure.AI.KnowledgeGraph.Neo4j;

/// <summary>
/// Neo4j implementation of <see cref="IKnowledgeGraphStore"/> using the Bolt driver
/// with Cypher queries for graph operations. Registered with keyed DI key <c>"neo4j"</c>.
/// </summary>
/// <remarks>
/// Requires a running Neo4j instance. Connection string format:
/// <c>bolt://host:7687</c> with credentials in <c>AppConfig.AI.Rag.GraphRag.ConnectionString</c>
/// formatted as <c>bolt://user:password@host:7687</c>.
/// </remarks>
public sealed class Neo4jGraphStore : IKnowledgeGraphStore, IAsyncDisposable
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.KnowledgeGraph.Neo4j");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IDriver _driver;
    private readonly ILogger<Neo4jGraphStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Neo4jGraphStore"/> class.
    /// </summary>
    /// <param name="configMonitor">Application configuration for Neo4j connection.</param>
    /// <param name="logger">Logger for recording graph operations.</param>
    public Neo4jGraphStore(
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<Neo4jGraphStore> logger)
    {
        ArgumentNullException.ThrowIfNull(configMonitor);
        ArgumentNullException.ThrowIfNull(logger);

        var connString = configMonitor.CurrentValue.AI.Rag.GraphRag.ConnectionString
            ?? throw new InvalidOperationException(
                "GraphRag.ConnectionString must be configured when using the 'neo4j' graph provider.");

        var uri = new Uri(connString);
        var userInfo = uri.UserInfo.Split(':');
        _driver = userInfo.Length == 2
            ? GraphDatabase.Driver(connString, AuthTokens.Basic(userInfo[0], userInfo[1]))
            : GraphDatabase.Driver(connString);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AddNodesAsync(
        IReadOnlyList<GraphNode> nodes,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("kg.neo4j.add_nodes");
        await using var session = _driver.AsyncSession();

        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("""
                    MERGE (n:Entity {id: $id})
                    SET n.name = $name, n.type = $type, n.properties = $props
                    WITH n
                    UNWIND $chunks AS chunkId
                    WITH n, collect(DISTINCT chunkId) + coalesce(n.chunk_ids, []) AS allChunks
                    SET n.chunk_ids = [x IN allChunks WHERE x IS NOT NULL | x]
                    """,
                    new
                    {
                        id = node.Id, name = node.Name, type = node.Type,
                        props = JsonSerializer.Serialize(node.Properties, JsonOptions),
                        chunks = node.ChunkIds.ToList()
                    });
            }, x => x.WithMetadata(new Dictionary<string, object?>()));
        }

        _logger.LogDebug("Neo4j: added/merged {Count} nodes", nodes.Count);
    }

    /// <inheritdoc />
    public async Task AddEdgesAsync(
        IReadOnlyList<GraphEdge> edges,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("kg.neo4j.add_edges");
        await using var session = _driver.AsyncSession();

        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("""
                    MATCH (s:Entity {id: $source}), (t:Entity {id: $target})
                    MERGE (s)-[r:RELATES {id: $id}]->(t)
                    SET r.predicate = $pred, r.properties = $props, r.chunk_id = $chunk
                    """,
                    new
                    {
                        id = edge.Id, source = edge.SourceNodeId,
                        target = edge.TargetNodeId, pred = edge.Predicate,
                        props = JsonSerializer.Serialize(edge.Properties, JsonOptions),
                        chunk = edge.ChunkId
                    });
            }, x => x.WithMetadata(new Dictionary<string, object?>()));
        }

        _logger.LogDebug("Neo4j: added {Count} edges", edges.Count);
    }

    /// <inheritdoc />
    public async Task<GraphNode?> GetNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (n:Entity {id: $id}) RETURN n", new { id = nodeId });
            if (await cursor.FetchAsync())
                return MapNode(cursor.Current["n"].As<INode>());
            return null;
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetNeighborsAsync(
        string nodeId,
        int maxDepth = 1,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("kg.neo4j.get_neighbors");
        await using var session = _driver.AsyncSession();

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                $"MATCH (start:Entity {{id: $id}})-[*1..{maxDepth}]-(neighbor:Entity) " +
                "WHERE neighbor.id <> $id RETURN DISTINCT neighbor",
                new { id = nodeId });

            var results = new List<GraphNode>();
            while (await cursor.FetchAsync())
                results.Add(MapNode(cursor.Current["neighbor"].As<INode>()));
            return (IReadOnlyList<GraphNode>)results;
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphTriplet>> GetTripletsAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default)
    {
        if (nodeIds.Count == 0) return [];

        await using var session = _driver.AsyncSession();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (s:Entity)-[r:RELATES]->(t:Entity)
                WHERE s.id IN $ids OR t.id IN $ids
                RETURN s, r, t
                """, new { ids = nodeIds.ToList() });

            var triplets = new List<GraphTriplet>();
            while (await cursor.FetchAsync())
            {
                triplets.Add(new GraphTriplet
                {
                    Source = MapNode(cursor.Current["s"].As<INode>()),
                    Edge = MapEdge(cursor.Current["r"].As<IRelationship>()),
                    Target = MapNode(cursor.Current["t"].As<INode>())
                });
            }
            return (IReadOnlyList<GraphTriplet>)triplets;
        });
    }

    /// <inheritdoc />
    public async Task<bool> NodeExistsAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (n:Entity {id: $id}) RETURN count(n) > 0 AS exists",
                new { id = nodeId });
            return await cursor.FetchAsync() && cursor.Current["exists"].As<bool>();
        });
    }

    /// <inheritdoc />
    public async Task DeleteNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (n:Entity {id: $id}) DETACH DELETE n",
                new { id = nodeId });
        });
    }

    /// <inheritdoc />
    public async Task DeleteEdgeAsync(
        string edgeId,
        CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH ()-[r:RELATES {id: $id}]->() DELETE r",
                new { id = edgeId });
        });
    }

    /// <inheritdoc />
    public async Task<int> GetNodeCountAsync(CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("MATCH (n:Entity) RETURN count(n) AS cnt");
            return await cursor.FetchAsync() ? cursor.Current["cnt"].As<int>() : 0;
        });
    }

    /// <inheritdoc />
    public async Task<int> GetEdgeCountAsync(CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("MATCH ()-[r:RELATES]->() RETURN count(r) AS cnt");
            return await cursor.FetchAsync() ? cursor.Current["cnt"].As<int>() : 0;
        });
    }

    /// <inheritdoc cref="IAsyncDisposable.DisposeAsync"/>
    public async ValueTask DisposeAsync()
    {
        await _driver.DisposeAsync();
    }

    private static GraphNode MapNode(INode neo4jNode)
    {
        var props = neo4jNode.Properties.TryGetValue("properties", out var p)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(p.As<string>(), JsonOptions)
              ?? new Dictionary<string, string>()
            : new Dictionary<string, string>();

        var chunks = neo4jNode.Properties.TryGetValue("chunk_ids", out var c)
            ? c.As<List<object>>().Select(x => x.ToString()!).ToList()
            : new List<string>();

        return new GraphNode
        {
            Id = neo4jNode.Properties["id"].As<string>(),
            Name = neo4jNode.Properties["name"].As<string>(),
            Type = neo4jNode.Properties["type"].As<string>(),
            Properties = props,
            ChunkIds = chunks
        };
    }

    private static GraphEdge MapEdge(IRelationship rel)
    {
        var props = rel.Properties.TryGetValue("properties", out var p)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(p.As<string>(), JsonOptions)
              ?? new Dictionary<string, string>()
            : new Dictionary<string, string>();

        return new GraphEdge
        {
            Id = rel.Properties["id"].As<string>(),
            SourceNodeId = rel.Properties.TryGetValue("source", out var s) ? s.As<string>() : "",
            TargetNodeId = rel.Properties.TryGetValue("target", out var t) ? t.As<string>() : "",
            Predicate = rel.Properties["predicate"].As<string>(),
            Properties = props,
            ChunkId = rel.Properties["chunk_id"].As<string>()
        };
    }
}

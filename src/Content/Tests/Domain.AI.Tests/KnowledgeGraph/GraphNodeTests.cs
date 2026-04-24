using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.KnowledgeGraph;

/// <summary>
/// Tests for <see cref="GraphNode"/> and <see cref="GraphEdge"/> records —
/// construction, defaults, equality, immutability, and with-expressions.
/// </summary>
public sealed class GraphNodeTests
{
    [Fact]
    public void Constructor_WithRequiredProperties_SetsValues()
    {
        var node = new GraphNode
        {
            Id = "node-1",
            Name = "Microsoft",
            Type = "Organization"
        };

        node.Id.Should().Be("node-1");
        node.Name.Should().Be("Microsoft");
        node.Type.Should().Be("Organization");
    }

    [Fact]
    public void Defaults_OptionalProperties_AreCorrect()
    {
        var node = new GraphNode { Id = "test", Name = "Test", Type = "Entity" };

        node.Properties.Should().BeEmpty();
        node.ChunkIds.Should().BeEmpty();
    }

    [Fact]
    public void WithExpression_CreatesNewInstance_PreservesOriginal()
    {
        var original = new GraphNode
        {
            Id = "node-1",
            Name = "Azure",
            Type = "Technology",
            ChunkIds = ["chunk-1"]
        };

        var modified = original with { Name = "Azure OpenAI" };

        modified.Name.Should().Be("Azure OpenAI");
        original.Name.Should().Be("Azure");
        modified.ChunkIds.Should().BeEquivalentTo(["chunk-1"]);
    }

    [Fact]
    public void Equality_SameValues_AreEquivalent()
    {
        var node1 = new GraphNode { Id = "n1", Name = "Test", Type = "Entity" };
        var node2 = new GraphNode { Id = "n1", Name = "Test", Type = "Entity" };

        node1.Should().BeEquivalentTo(node2);
    }

    [Fact]
    public void Equality_DifferentIds_AreNotEquivalent()
    {
        var node1 = new GraphNode { Id = "n1", Name = "Test", Type = "Entity" };
        var node2 = new GraphNode { Id = "n2", Name = "Test", Type = "Entity" };

        node1.Should().NotBeEquivalentTo(node2);
    }

    [Fact]
    public void Properties_CanBePopulated()
    {
        var node = new GraphNode
        {
            Id = "person-1",
            Name = "Satya Nadella",
            Type = "Person",
            Properties = new Dictionary<string, string>
            {
                ["role"] = "CEO",
                ["company"] = "Microsoft"
            }
        };

        node.Properties.Should().HaveCount(2);
        node.Properties["role"].Should().Be("CEO");
    }

    [Fact]
    public void ChunkIds_MultipleChunks_PreservesAll()
    {
        var node = new GraphNode
        {
            Id = "tech-1",
            Name = "OAuth 2.0",
            Type = "Technology",
            ChunkIds = ["chunk-a", "chunk-b", "chunk-c"]
        };

        node.ChunkIds.Should().HaveCount(3);
        node.ChunkIds.Should().ContainInOrder("chunk-a", "chunk-b", "chunk-c");
    }
}

/// <summary>
/// Tests for <see cref="GraphEdge"/> record — construction, defaults, and equality.
/// </summary>
public sealed class GraphEdgeTests
{
    [Fact]
    public void Constructor_WithRequiredProperties_SetsValues()
    {
        var edge = new GraphEdge
        {
            Id = "edge-1",
            SourceNodeId = "node-a",
            TargetNodeId = "node-b",
            Predicate = "uses",
            ChunkId = "chunk-1"
        };

        edge.Id.Should().Be("edge-1");
        edge.SourceNodeId.Should().Be("node-a");
        edge.TargetNodeId.Should().Be("node-b");
        edge.Predicate.Should().Be("uses");
        edge.ChunkId.Should().Be("chunk-1");
    }

    [Fact]
    public void Defaults_Properties_IsEmpty()
    {
        var edge = new GraphEdge
        {
            Id = "e1",
            SourceNodeId = "n1",
            TargetNodeId = "n2",
            Predicate = "related_to",
            ChunkId = "c1"
        };

        edge.Properties.Should().BeEmpty();
    }

    [Fact]
    public void Equality_SameValues_AreEquivalent()
    {
        var edge1 = new GraphEdge
        {
            Id = "e1", SourceNodeId = "n1", TargetNodeId = "n2",
            Predicate = "uses", ChunkId = "c1"
        };
        var edge2 = new GraphEdge
        {
            Id = "e1", SourceNodeId = "n1", TargetNodeId = "n2",
            Predicate = "uses", ChunkId = "c1"
        };

        edge1.Should().BeEquivalentTo(edge2);
    }
}

/// <summary>
/// Tests for <see cref="GraphTriplet"/> record — construction and composition.
/// </summary>
public sealed class GraphTripletTests
{
    [Fact]
    public void Constructor_ComposesNodeEdgeNode()
    {
        var source = new GraphNode { Id = "n1", Name = "Azure", Type = "Technology" };
        var target = new GraphNode { Id = "n2", Name = "OpenAI", Type = "Organization" };
        var edge = new GraphEdge
        {
            Id = "e1", SourceNodeId = "n1", TargetNodeId = "n2",
            Predicate = "integrates_with", ChunkId = "c1"
        };

        var triplet = new GraphTriplet { Source = source, Edge = edge, Target = target };

        triplet.Source.Name.Should().Be("Azure");
        triplet.Edge.Predicate.Should().Be("integrates_with");
        triplet.Target.Name.Should().Be("OpenAI");
    }
}

/// <summary>
/// Tests for <see cref="NodeFeedbackWeight"/> and <see cref="EdgeFeedbackWeight"/> records.
/// </summary>
public sealed class FeedbackWeightTests
{
    [Fact]
    public void NodeFeedbackWeight_Construction_SetsAllProperties()
    {
        var now = DateTimeOffset.UtcNow;
        var weight = new NodeFeedbackWeight
        {
            NodeId = "node-1",
            Weight = 0.75,
            UpdateCount = 5,
            LastUpdatedAt = now
        };

        weight.NodeId.Should().Be("node-1");
        weight.Weight.Should().Be(0.75);
        weight.UpdateCount.Should().Be(5);
        weight.LastUpdatedAt.Should().Be(now);
    }

    [Fact]
    public void EdgeFeedbackWeight_Construction_SetsAllProperties()
    {
        var now = DateTimeOffset.UtcNow;
        var weight = new EdgeFeedbackWeight
        {
            EdgeId = "edge-1",
            Weight = 0.3,
            UpdateCount = 2,
            LastUpdatedAt = now
        };

        weight.EdgeId.Should().Be("edge-1");
        weight.Weight.Should().Be(0.3);
        weight.UpdateCount.Should().Be(2);
        weight.LastUpdatedAt.Should().Be(now);
    }
}

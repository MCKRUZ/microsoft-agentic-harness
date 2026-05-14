using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.DriftDetection;
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.DriftDetection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.DriftDetection;

public sealed class GraphDriftBaselineStoreTests
{
    private readonly Mock<IKnowledgeGraphStore> _graphStoreMock = new();
    private readonly Mock<ILogger<GraphDriftBaselineStore>> _loggerMock = new();

    private GraphDriftBaselineStore CreateStore() =>
        new(_graphStoreMock.Object, _loggerMock.Object);

    private static DriftBaseline CreateBaseline(
        DriftScope scope = DriftScope.Skill,
        string scopeIdentifier = "code_review",
        int sampleCount = 10) => new()
    {
        BaselineId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        Scope = scope,
        ScopeIdentifier = scopeIdentifier,
        Dimensions = new Dictionary<DriftDimension, double>
        {
            [DriftDimension.Faithfulness] = 0.85,
            [DriftDimension.Relevance] = 0.90
        }.AsReadOnly(),
        DimensionSigmas = new Dictionary<DriftDimension, double>
        {
            [DriftDimension.Faithfulness] = 0.05,
            [DriftDimension.Relevance] = 0.03
        }.AsReadOnly(),
        SampleCount = sampleCount,
        WindowStart = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
        WindowEnd = new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero),
        CreatedAt = new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero)
    };

    [Fact]
    public async Task SaveBaseline_CreatesNodeWithDeterministicId()
    {
        // Arrange
        var baseline = CreateBaseline();
        var capturedNodes = new List<GraphNode>();

        _graphStoreMock
            .Setup(g => g.AddNodesAsync(It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<GraphNode>, CancellationToken>((nodes, _) => capturedNodes.AddRange(nodes))
            .Returns(Task.CompletedTask);

        _graphStoreMock
            .Setup(g => g.AddEdgesAsync(It.IsAny<IReadOnlyList<GraphEdge>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var store = CreateStore();

        // Act
        var result = await store.SaveBaselineAsync(baseline, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var baselineNode = capturedNodes.FirstOrDefault(n => n.Type == "DriftBaseline");
        baselineNode.Should().NotBeNull();
        baselineNode!.Id.Should().Be("driftbaseline:skill:code_review");
        baselineNode.Properties.Should().ContainKey("BaselineId");
        baselineNode.Properties.Should().ContainKey("Dimensions");
        baselineNode.Properties.Should().ContainKey("DimensionSigmas");
        baselineNode.Properties["SampleCount"].Should().Be("10");
    }

    [Fact]
    public async Task GetBaseline_ExistingNode_DeserializesBaseline()
    {
        // Arrange
        var baseline = CreateBaseline(DriftScope.Agent, "agent-1");
        var expectedId = "driftbaseline:agent:agent-1";

        var node = new GraphNode
        {
            Id = expectedId,
            Name = "DriftBaseline:Agent:agent-1",
            Type = "DriftBaseline",
            Properties = new Dictionary<string, string>
            {
                ["BaselineId"] = baseline.BaselineId.ToString(),
                ["Scope"] = "Agent",
                ["ScopeIdentifier"] = "agent-1",
                ["Dimensions"] = "{\"Faithfulness\":0.85,\"Relevance\":0.9}",
                ["DimensionSigmas"] = "{\"Faithfulness\":0.05,\"Relevance\":0.03}",
                ["SampleCount"] = "10",
                ["WindowStart"] = "2026-05-01T00:00:00+00:00",
                ["WindowEnd"] = "2026-05-10T00:00:00+00:00",
                ["CreatedAt"] = "2026-05-10T12:00:00+00:00"
            }.AsReadOnly()
        };

        _graphStoreMock
            .Setup(g => g.GetNodeAsync(expectedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(node);

        var store = CreateStore();

        // Act
        var result = await store.GetBaselineAsync(DriftScope.Agent, "agent-1", CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.BaselineId.Should().Be(baseline.BaselineId);
        result.Value.Scope.Should().Be(DriftScope.Agent);
        result.Value.ScopeIdentifier.Should().Be("agent-1");
        result.Value.Dimensions.Should().HaveCount(2);
        result.Value.Dimensions[DriftDimension.Faithfulness].Should().BeApproximately(0.85, 1e-10);
        result.Value.DimensionSigmas[DriftDimension.Faithfulness].Should().BeApproximately(0.05, 1e-10);
        result.Value.SampleCount.Should().Be(10);
    }

    [Fact]
    public async Task GetBaseline_NotFound_ReturnsNull()
    {
        // Arrange
        _graphStoreMock
            .Setup(g => g.GetNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GraphNode?)null);

        var store = CreateStore();

        // Act
        var result = await store.GetBaselineAsync(DriftScope.Skill, "nonexistent", CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task SaveBaseline_OverwritesExisting()
    {
        // Arrange
        _graphStoreMock
            .Setup(g => g.AddNodesAsync(It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _graphStoreMock
            .Setup(g => g.AddEdgesAsync(It.IsAny<IReadOnlyList<GraphEdge>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var store = CreateStore();

        var v1 = CreateBaseline(DriftScope.TaskType, "summarization", sampleCount: 5);
        var v2 = CreateBaseline(DriftScope.TaskType, "summarization", sampleCount: 20);

        // Act
        await store.SaveBaselineAsync(v1, CancellationToken.None);
        var result = await store.SaveBaselineAsync(v2, CancellationToken.None);

        // Assert — both saves use the same deterministic ID (upsert semantics)
        result.IsSuccess.Should().BeTrue();
        _graphStoreMock.Verify(g => g.AddNodesAsync(
            It.Is<IReadOnlyList<GraphNode>>(nodes =>
                nodes[0].Id == "driftbaseline:tasktype:summarization"),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetBaselines_ByScope_ReturnsFiltered()
    {
        // Arrange
        var allNodes = new List<GraphNode>
        {
            CreateBaselineNode("driftbaseline:skill:code_review", DriftScope.Skill, "code_review"),
            CreateBaselineNode("driftbaseline:skill:summarize", DriftScope.Skill, "summarize"),
            CreateBaselineNode("driftbaseline:agent:agent-1", DriftScope.Agent, "agent-1")
        };

        _graphStoreMock
            .Setup(g => g.GetAllNodesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(allNodes.AsReadOnly());

        var store = CreateStore();

        // Act
        var result = await store.GetBaselinesAsync(DriftScope.Skill, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(2);
        result.Value.Should().OnlyContain(b => b.Scope == DriftScope.Skill);
    }

    [Fact]
    public async Task GetBaselines_NullScope_ReturnsAll()
    {
        // Arrange
        var allNodes = new List<GraphNode>
        {
            CreateBaselineNode("driftbaseline:skill:code_review", DriftScope.Skill, "code_review"),
            CreateBaselineNode("driftbaseline:agent:agent-1", DriftScope.Agent, "agent-1")
        };

        _graphStoreMock
            .Setup(g => g.GetAllNodesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(allNodes.AsReadOnly());

        var store = CreateStore();

        // Act
        var result = await store.GetBaselinesAsync(null, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(2);
    }

    [Fact]
    public async Task SaveBaseline_CreatesEdge()
    {
        // Arrange
        var baseline = CreateBaseline();
        GraphEdge? capturedEdge = null;

        _graphStoreMock
            .Setup(g => g.AddNodesAsync(It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _graphStoreMock
            .Setup(g => g.AddEdgesAsync(It.IsAny<IReadOnlyList<GraphEdge>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<GraphEdge>, CancellationToken>((edges, _) => capturedEdge = edges[0])
            .Returns(Task.CompletedTask);

        var store = CreateStore();

        // Act
        await store.SaveBaselineAsync(baseline, CancellationToken.None);

        // Assert
        capturedEdge.Should().NotBeNull();
        capturedEdge!.Predicate.Should().Be("baseline_for");
        capturedEdge.SourceNodeId.Should().Be("driftbaseline:skill:code_review");
        capturedEdge.TargetNodeId.Should().Be("scope:skill:code_review");
    }

    [Fact]
    public async Task GetBaseline_GraphStoreThrows_ReturnsFailure()
    {
        // Arrange
        _graphStoreMock
            .Setup(g => g.GetNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection lost"));

        var store = CreateStore();

        // Act
        var result = await store.GetBaselineAsync(DriftScope.Skill, "code_review", CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("connection lost");
    }

    [Fact]
    public async Task GetBaselines_GraphStoreThrows_ReturnsFailure()
    {
        // Arrange
        _graphStoreMock
            .Setup(g => g.GetAllNodesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection lost"));

        var store = CreateStore();

        // Act
        var result = await store.GetBaselinesAsync(DriftScope.Skill, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("connection lost");
    }

    [Fact]
    public async Task SaveBaseline_GraphStoreThrows_ReturnsFailure()
    {
        // Arrange
        _graphStoreMock
            .Setup(g => g.AddNodesAsync(It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("write failed"));

        var store = CreateStore();

        // Act
        var result = await store.SaveBaselineAsync(CreateBaseline(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("write failed");
    }

    private static GraphNode CreateBaselineNode(string id, DriftScope scope, string identifier) => new()
    {
        Id = id,
        Name = $"DriftBaseline:{scope}:{identifier}",
        Type = "DriftBaseline",
        Properties = new Dictionary<string, string>
        {
            ["BaselineId"] = Guid.NewGuid().ToString(),
            ["Scope"] = scope.ToString(),
            ["ScopeIdentifier"] = identifier,
            ["Dimensions"] = "{\"Faithfulness\":0.85,\"Relevance\":0.9}",
            ["DimensionSigmas"] = "{\"Faithfulness\":0.05,\"Relevance\":0.03}",
            ["SampleCount"] = "10",
            ["WindowStart"] = "2026-05-01T00:00:00+00:00",
            ["WindowEnd"] = "2026-05-10T00:00:00+00:00",
            ["CreatedAt"] = "2026-05-10T12:00:00+00:00"
        }.AsReadOnly()
    };
}

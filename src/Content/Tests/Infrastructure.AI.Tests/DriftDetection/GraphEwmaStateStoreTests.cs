using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.DriftDetection;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using FluentAssertions;
using Infrastructure.AI.DriftDetection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.DriftDetection;

public sealed class GraphEwmaStateStoreTests
{
    private readonly Mock<IKnowledgeGraphStore> _graphStoreMock = new();
    private readonly Mock<ILogger<GraphEwmaStateStore>> _loggerMock = new();

    private GraphEwmaStateStore CreateStore() =>
        new(_graphStoreMock.Object, _loggerMock.Object);

    [Fact]
    public async Task GetState_ExistingNode_DeserializesEwmaState()
    {
        // Arrange
        var expectedId = "ewma:Skill:code_review:Faithfulness";
        var node = new GraphNode
        {
            Id = expectedId,
            Name = expectedId,
            Type = "EwmaState",
            Properties = new Dictionary<string, string>
            {
                ["Scope"] = "Skill",
                ["ScopeIdentifier"] = "code_review",
                ["Dimension"] = "Faithfulness",
                ["CurrentEwma"] = "0.79",
                ["SampleCount"] = "5",
                ["LastUpdatedAt"] = "2026-05-11T12:00:00+00:00"
            }.AsReadOnly()
        };

        _graphStoreMock
            .Setup(g => g.GetNodeAsync(expectedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(node);

        var store = CreateStore();

        // Act
        var result = await store.GetStateAsync(
            DriftScope.Skill, "code_review", DriftDimension.Faithfulness, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Scope.Should().Be(DriftScope.Skill);
        result.Value.ScopeIdentifier.Should().Be("code_review");
        result.Value.Dimension.Should().Be(DriftDimension.Faithfulness);
        result.Value.CurrentEwma.Should().BeApproximately(0.79, 1e-10);
        result.Value.SampleCount.Should().Be(5);
    }

    [Fact]
    public async Task GetState_NoNode_ReturnsNull()
    {
        // Arrange
        _graphStoreMock
            .Setup(g => g.GetNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GraphNode?)null);

        var store = CreateStore();

        // Act
        var result = await store.GetStateAsync(
            DriftScope.Skill, "code_review", DriftDimension.Faithfulness, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task SaveState_CreatesGraphNodeWithDeterministicId()
    {
        // Arrange
        var state = new EwmaState
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Dimension = DriftDimension.Faithfulness,
            CurrentEwma = 0.79,
            SampleCount = 5,
            LastUpdatedAt = new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero)
        };

        _graphStoreMock
            .Setup(g => g.AddNodesAsync(It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var store = CreateStore();

        // Act
        var result = await store.SaveStateAsync(state, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        _graphStoreMock.Verify(g => g.AddNodesAsync(
            It.Is<IReadOnlyList<GraphNode>>(nodes =>
                nodes.Count == 1 &&
                nodes[0].Id == "ewma:Skill:code_review:Faithfulness" &&
                nodes[0].Type == "EwmaState" &&
                nodes[0].Properties["CurrentEwma"] == "0.79" &&
                nodes[0].Properties["SampleCount"] == "5"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveState_OverwritesExistingNode()
    {
        // Arrange — saving again with updated values
        var state = new EwmaState
        {
            Scope = DriftScope.Skill,
            ScopeIdentifier = "code_review",
            Dimension = DriftDimension.Faithfulness,
            CurrentEwma = 0.75,
            SampleCount = 10,
            LastUpdatedAt = new DateTimeOffset(2026, 5, 11, 13, 0, 0, TimeSpan.Zero)
        };

        _graphStoreMock
            .Setup(g => g.AddNodesAsync(It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var store = CreateStore();

        // Act
        var result = await store.SaveStateAsync(state, CancellationToken.None);

        // Assert — same deterministic ID means upsert semantics
        result.IsSuccess.Should().BeTrue();
        _graphStoreMock.Verify(g => g.AddNodesAsync(
            It.Is<IReadOnlyList<GraphNode>>(nodes =>
                nodes[0].Id == "ewma:Skill:code_review:Faithfulness" &&
                nodes[0].Properties["CurrentEwma"] == "0.75" &&
                nodes[0].Properties["SampleCount"] == "10"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetStates_ReturnsAllDimensionsForScope()
    {
        // Arrange — two dimensions have state, rest return null
        var faithfulnessNode = new GraphNode
        {
            Id = "ewma:Skill:code_review:Faithfulness",
            Name = "ewma:Skill:code_review:Faithfulness",
            Type = "EwmaState",
            Properties = new Dictionary<string, string>
            {
                ["Scope"] = "Skill",
                ["ScopeIdentifier"] = "code_review",
                ["Dimension"] = "Faithfulness",
                ["CurrentEwma"] = "0.79",
                ["SampleCount"] = "5",
                ["LastUpdatedAt"] = "2026-05-11T12:00:00+00:00"
            }.AsReadOnly()
        };

        var relevanceNode = new GraphNode
        {
            Id = "ewma:Skill:code_review:Relevance",
            Name = "ewma:Skill:code_review:Relevance",
            Type = "EwmaState",
            Properties = new Dictionary<string, string>
            {
                ["Scope"] = "Skill",
                ["ScopeIdentifier"] = "code_review",
                ["Dimension"] = "Relevance",
                ["CurrentEwma"] = "0.85",
                ["SampleCount"] = "3",
                ["LastUpdatedAt"] = "2026-05-11T11:00:00+00:00"
            }.AsReadOnly()
        };

        _graphStoreMock
            .Setup(g => g.GetNodeAsync("ewma:Skill:code_review:Faithfulness", It.IsAny<CancellationToken>()))
            .ReturnsAsync(faithfulnessNode);

        _graphStoreMock
            .Setup(g => g.GetNodeAsync("ewma:Skill:code_review:Relevance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(relevanceNode);

        _graphStoreMock
            .Setup(g => g.GetNodeAsync(It.Is<string>(id =>
                id != "ewma:Skill:code_review:Faithfulness" &&
                id != "ewma:Skill:code_review:Relevance"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GraphNode?)null);

        var store = CreateStore();

        // Act
        var result = await store.GetStatesAsync(
            DriftScope.Skill, "code_review", CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(2);
        result.Value.Should().Contain(s => s.Dimension == DriftDimension.Faithfulness);
        result.Value.Should().Contain(s => s.Dimension == DriftDimension.Relevance);
    }
}

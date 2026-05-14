diff --git a/src/Content/Infrastructure/Infrastructure.AI/DriftDetection/GraphDriftBaselineStore.cs b/src/Content/Infrastructure/Infrastructure.AI/DriftDetection/GraphDriftBaselineStore.cs
new file mode 100644
index 0000000..b360735
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/DriftDetection/GraphDriftBaselineStore.cs
@@ -0,0 +1,170 @@
+using System.Globalization;
+using System.Text.Json;
+using System.Text.Json.Serialization;
+using Application.AI.Common.Interfaces.DriftDetection;
+using Application.AI.Common.Interfaces.KnowledgeGraph;
+using Domain.AI.DriftDetection;
+using Domain.AI.KnowledgeGraph.Models;
+using Domain.Common;
+using Microsoft.Extensions.Logging;
+
+namespace Infrastructure.AI.DriftDetection;
+
+/// <summary>
+/// Graph-backed persistence for drift baselines. Uses <see cref="IKnowledgeGraphStore"/>
+/// with deterministic node IDs (<c>"driftbaseline:{scope}:{identifier}"</c>) for O(1) lookups.
+/// </summary>
+/// <remarks>
+/// <para>
+/// Each baseline is stored as a <see cref="GraphNode"/> with <c>Type = "DriftBaseline"</c>.
+/// Complex properties (<see cref="DriftBaseline.Dimensions"/>, <see cref="DriftBaseline.DimensionSigmas"/>)
+/// are JSON-serialized into the node's <see cref="GraphNode.Properties"/> dictionary.
+/// </para>
+/// <para>
+/// A <c>"baseline_for"</c> edge connects each baseline node to a scope identifier node,
+/// enabling graph traversal queries that discover all baselines for a given scope entity.
+/// </para>
+/// </remarks>
+public sealed class GraphDriftBaselineStore : IDriftBaselineStore
+{
+    private static readonly JsonSerializerOptions s_jsonOptions = new()
+    {
+        Converters = { new JsonStringEnumConverter() }
+    };
+
+    private readonly IKnowledgeGraphStore _graphStore;
+    private readonly ILogger<GraphDriftBaselineStore> _logger;
+
+    /// <summary>
+    /// Initializes a new instance of <see cref="GraphDriftBaselineStore"/>.
+    /// </summary>
+    /// <param name="graphStore">The knowledge graph backend for node/edge persistence.</param>
+    /// <param name="logger">Logger for error diagnostics.</param>
+    public GraphDriftBaselineStore(
+        IKnowledgeGraphStore graphStore,
+        ILogger<GraphDriftBaselineStore> logger)
+    {
+        _graphStore = graphStore;
+        _logger = logger;
+    }
+
+    /// <inheritdoc />
+    public async Task<Result> SaveBaselineAsync(DriftBaseline baseline, CancellationToken ct)
+    {
+        var nodeId = BuildId(baseline.Scope, baseline.ScopeIdentifier);
+
+        try
+        {
+            var node = SerializeBaseline(baseline, nodeId);
+            await _graphStore.AddNodesAsync([node], ct);
+
+            var scopeNodeId = $"scope:{baseline.Scope.ToString().ToLowerInvariant()}:{baseline.ScopeIdentifier.ToLowerInvariant()}";
+            var scopeNode = new GraphNode
+            {
+                Id = scopeNodeId,
+                Name = $"{baseline.Scope}:{baseline.ScopeIdentifier}",
+                Type = "ScopeIdentifier"
+            };
+            await _graphStore.AddNodesAsync([scopeNode], ct);
+
+            var edge = new GraphEdge
+            {
+                Id = $"{nodeId}->baseline_for->{scopeNodeId}",
+                SourceNodeId = nodeId,
+                TargetNodeId = scopeNodeId,
+                Predicate = "baseline_for",
+                ChunkId = nodeId
+            };
+            await _graphStore.AddEdgesAsync([edge], ct);
+
+            return Result.Success();
+        }
+        catch (Exception ex)
+        {
+            _logger.LogError(ex, "Failed to save drift baseline for {Id}", nodeId);
+            return Result.Fail($"Failed to save drift baseline: {ex.Message}");
+        }
+    }
+
+    /// <inheritdoc />
+    public async Task<Result<DriftBaseline?>> GetBaselineAsync(
+        DriftScope scope, string scopeIdentifier, CancellationToken ct)
+    {
+        var nodeId = BuildId(scope, scopeIdentifier);
+
+        try
+        {
+            var node = await _graphStore.GetNodeAsync(nodeId, ct);
+            if (node is null)
+                return Result<DriftBaseline?>.Success(null);
+
+            var baseline = DeserializeBaseline(node);
+            return Result<DriftBaseline?>.Success(baseline);
+        }
+        catch (Exception ex)
+        {
+            _logger.LogError(ex, "Failed to get drift baseline for {Id}", nodeId);
+            return Result<DriftBaseline?>.Fail($"Failed to retrieve drift baseline: {ex.Message}");
+        }
+    }
+
+    /// <inheritdoc />
+    public async Task<Result<IReadOnlyList<DriftBaseline>>> GetBaselinesAsync(
+        DriftScope? scope, CancellationToken ct)
+    {
+        try
+        {
+            var allNodes = await _graphStore.GetAllNodesAsync(ct);
+
+            var baselines = allNodes
+                .Where(n => n.Type == "DriftBaseline")
+                .Where(n => scope is null || n.Properties.TryGetValue("Scope", out var s) && Enum.TryParse<DriftScope>(s, out var parsed) && parsed == scope)
+                .Select(DeserializeBaseline)
+                .ToList();
+
+            return Result<IReadOnlyList<DriftBaseline>>.Success(baselines.AsReadOnly());
+        }
+        catch (Exception ex)
+        {
+            _logger.LogError(ex, "Failed to get drift baselines for scope {Scope}", scope);
+            return Result<IReadOnlyList<DriftBaseline>>.Fail($"Failed to retrieve drift baselines: {ex.Message}");
+        }
+    }
+
+    private static string BuildId(DriftScope scope, string scopeIdentifier) =>
+        $"driftbaseline:{scope.ToString().ToLowerInvariant()}:{scopeIdentifier.ToLowerInvariant()}";
+
+    private static GraphNode SerializeBaseline(DriftBaseline baseline, string nodeId) => new()
+    {
+        Id = nodeId,
+        Name = $"DriftBaseline:{baseline.Scope}:{baseline.ScopeIdentifier}",
+        Type = "DriftBaseline",
+        Properties = new Dictionary<string, string>
+        {
+            ["BaselineId"] = baseline.BaselineId.ToString(),
+            ["Scope"] = baseline.Scope.ToString(),
+            ["ScopeIdentifier"] = baseline.ScopeIdentifier,
+            ["Dimensions"] = JsonSerializer.Serialize(baseline.Dimensions, s_jsonOptions),
+            ["DimensionSigmas"] = JsonSerializer.Serialize(baseline.DimensionSigmas, s_jsonOptions),
+            ["SampleCount"] = baseline.SampleCount.ToString(CultureInfo.InvariantCulture),
+            ["WindowStart"] = baseline.WindowStart.ToString("o"),
+            ["WindowEnd"] = baseline.WindowEnd.ToString("o"),
+            ["CreatedAt"] = baseline.CreatedAt.ToString("o")
+        }.AsReadOnly()
+    };
+
+    private static DriftBaseline DeserializeBaseline(GraphNode node) => new()
+    {
+        BaselineId = Guid.Parse(node.Properties["BaselineId"]),
+        Scope = Enum.Parse<DriftScope>(node.Properties["Scope"]),
+        ScopeIdentifier = node.Properties["ScopeIdentifier"],
+        Dimensions = JsonSerializer.Deserialize<Dictionary<DriftDimension, double>>(
+            node.Properties["Dimensions"], s_jsonOptions)!.AsReadOnly(),
+        DimensionSigmas = JsonSerializer.Deserialize<Dictionary<DriftDimension, double>>(
+            node.Properties["DimensionSigmas"], s_jsonOptions)!.AsReadOnly(),
+        SampleCount = int.Parse(node.Properties["SampleCount"], CultureInfo.InvariantCulture),
+        WindowStart = DateTimeOffset.Parse(node.Properties["WindowStart"], CultureInfo.InvariantCulture),
+        WindowEnd = DateTimeOffset.Parse(node.Properties["WindowEnd"], CultureInfo.InvariantCulture),
+        CreatedAt = DateTimeOffset.Parse(node.Properties["CreatedAt"], CultureInfo.InvariantCulture)
+    };
+}
diff --git a/src/Content/Infrastructure/Infrastructure.AI/DriftDetection/InMemoryDriftBaselineStore.cs b/src/Content/Infrastructure/Infrastructure.AI/DriftDetection/InMemoryDriftBaselineStore.cs
new file mode 100644
index 0000000..7f549f8
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/DriftDetection/InMemoryDriftBaselineStore.cs
@@ -0,0 +1,47 @@
+using System.Collections.Concurrent;
+using Application.AI.Common.Interfaces.DriftDetection;
+using Domain.AI.DriftDetection;
+using Domain.Common;
+
+namespace Infrastructure.AI.DriftDetection;
+
+/// <summary>
+/// In-memory baseline store backed by <see cref="ConcurrentDictionary{TKey,TValue}"/>
+/// for thread-safe operation. Intended for testing and development scenarios where
+/// graph persistence is unnecessary.
+/// </summary>
+/// <remarks>
+/// Uses a composite key of <c>(<see cref="DriftScope"/>, string scopeIdentifier)</c>
+/// for O(1) lookups. All operations return <see cref="Result.Success()"/> — this store
+/// cannot fail under normal conditions. Data is not persisted across process restarts.
+/// </remarks>
+public sealed class InMemoryDriftBaselineStore : IDriftBaselineStore
+{
+    private readonly ConcurrentDictionary<(DriftScope Scope, string Identifier), DriftBaseline> _baselines = new();
+
+    /// <inheritdoc />
+    public Task<Result> SaveBaselineAsync(DriftBaseline baseline, CancellationToken ct)
+    {
+        _baselines[(baseline.Scope, baseline.ScopeIdentifier)] = baseline;
+        return Task.FromResult(Result.Success());
+    }
+
+    /// <inheritdoc />
+    public Task<Result<DriftBaseline?>> GetBaselineAsync(
+        DriftScope scope, string scopeIdentifier, CancellationToken ct)
+    {
+        _baselines.TryGetValue((scope, scopeIdentifier), out var baseline);
+        return Task.FromResult(Result<DriftBaseline?>.Success(baseline));
+    }
+
+    /// <inheritdoc />
+    public Task<Result<IReadOnlyList<DriftBaseline>>> GetBaselinesAsync(
+        DriftScope? scope, CancellationToken ct)
+    {
+        var results = scope is null
+            ? _baselines.Values.ToList()
+            : _baselines.Values.Where(b => b.Scope == scope.Value).ToList();
+
+        return Task.FromResult(Result<IReadOnlyList<DriftBaseline>>.Success(results.AsReadOnly()));
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/GraphDriftBaselineStoreTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/GraphDriftBaselineStoreTests.cs
new file mode 100644
index 0000000..7cca809
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/GraphDriftBaselineStoreTests.cs
@@ -0,0 +1,301 @@
+using Application.AI.Common.Interfaces.KnowledgeGraph;
+using Domain.AI.DriftDetection;
+using Domain.AI.KnowledgeGraph.Models;
+using FluentAssertions;
+using Infrastructure.AI.DriftDetection;
+using Microsoft.Extensions.Logging;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.DriftDetection;
+
+public sealed class GraphDriftBaselineStoreTests
+{
+    private readonly Mock<IKnowledgeGraphStore> _graphStoreMock = new();
+    private readonly Mock<ILogger<GraphDriftBaselineStore>> _loggerMock = new();
+
+    private GraphDriftBaselineStore CreateStore() =>
+        new(_graphStoreMock.Object, _loggerMock.Object);
+
+    private static DriftBaseline CreateBaseline(
+        DriftScope scope = DriftScope.Skill,
+        string scopeIdentifier = "code_review",
+        int sampleCount = 10) => new()
+    {
+        BaselineId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
+        Scope = scope,
+        ScopeIdentifier = scopeIdentifier,
+        Dimensions = new Dictionary<DriftDimension, double>
+        {
+            [DriftDimension.Faithfulness] = 0.85,
+            [DriftDimension.Relevance] = 0.90
+        }.AsReadOnly(),
+        DimensionSigmas = new Dictionary<DriftDimension, double>
+        {
+            [DriftDimension.Faithfulness] = 0.05,
+            [DriftDimension.Relevance] = 0.03
+        }.AsReadOnly(),
+        SampleCount = sampleCount,
+        WindowStart = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
+        WindowEnd = new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero),
+        CreatedAt = new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero)
+    };
+
+    [Fact]
+    public async Task SaveBaseline_CreatesNodeWithDeterministicId()
+    {
+        // Arrange
+        var baseline = CreateBaseline();
+        var capturedNodes = new List<GraphNode>();
+
+        _graphStoreMock
+            .Setup(g => g.AddNodesAsync(It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()))
+            .Callback<IReadOnlyList<GraphNode>, CancellationToken>((nodes, _) => capturedNodes.AddRange(nodes))
+            .Returns(Task.CompletedTask);
+
+        _graphStoreMock
+            .Setup(g => g.AddEdgesAsync(It.IsAny<IReadOnlyList<GraphEdge>>(), It.IsAny<CancellationToken>()))
+            .Returns(Task.CompletedTask);
+
+        var store = CreateStore();
+
+        // Act
+        var result = await store.SaveBaselineAsync(baseline, CancellationToken.None);
+
+        // Assert
+        result.IsSuccess.Should().BeTrue();
+        var baselineNode = capturedNodes.FirstOrDefault(n => n.Type == "DriftBaseline");
+        baselineNode.Should().NotBeNull();
+        baselineNode!.Id.Should().Be("driftbaseline:skill:code_review");
+        baselineNode.Properties.Should().ContainKey("BaselineId");
+        baselineNode.Properties.Should().ContainKey("Dimensions");
+        baselineNode.Properties.Should().ContainKey("DimensionSigmas");
+        baselineNode.Properties["SampleCount"].Should().Be("10");
+    }
+
+    [Fact]
+    public async Task GetBaseline_ExistingNode_DeserializesBaseline()
+    {
+        // Arrange
+        var baseline = CreateBaseline(DriftScope.Agent, "agent-1");
+        var expectedId = "driftbaseline:agent:agent-1";
+
+        var node = new GraphNode
+        {
+            Id = expectedId,
+            Name = "DriftBaseline:Agent:agent-1",
+            Type = "DriftBaseline",
+            Properties = new Dictionary<string, string>
+            {
+                ["BaselineId"] = baseline.BaselineId.ToString(),
+                ["Scope"] = "Agent",
+                ["ScopeIdentifier"] = "agent-1",
+                ["Dimensions"] = "{\"Faithfulness\":0.85,\"Relevance\":0.9}",
+                ["DimensionSigmas"] = "{\"Faithfulness\":0.05,\"Relevance\":0.03}",
+                ["SampleCount"] = "10",
+                ["WindowStart"] = "2026-05-01T00:00:00+00:00",
+                ["WindowEnd"] = "2026-05-10T00:00:00+00:00",
+                ["CreatedAt"] = "2026-05-10T12:00:00+00:00"
+            }.AsReadOnly()
+        };
+
+        _graphStoreMock
+            .Setup(g => g.GetNodeAsync(expectedId, It.IsAny<CancellationToken>()))
+            .ReturnsAsync(node);
+
+        var store = CreateStore();
+
+        // Act
+        var result = await store.GetBaselineAsync(DriftScope.Agent, "agent-1", CancellationToken.None);
+
+        // Assert
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().NotBeNull();
+        result.Value!.BaselineId.Should().Be(baseline.BaselineId);
+        result.Value.Scope.Should().Be(DriftScope.Agent);
+        result.Value.ScopeIdentifier.Should().Be("agent-1");
+        result.Value.Dimensions.Should().HaveCount(2);
+        result.Value.Dimensions[DriftDimension.Faithfulness].Should().BeApproximately(0.85, 1e-10);
+        result.Value.DimensionSigmas[DriftDimension.Faithfulness].Should().BeApproximately(0.05, 1e-10);
+        result.Value.SampleCount.Should().Be(10);
+    }
+
+    [Fact]
+    public async Task GetBaseline_NotFound_ReturnsNull()
+    {
+        // Arrange
+        _graphStoreMock
+            .Setup(g => g.GetNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync((GraphNode?)null);
+
+        var store = CreateStore();
+
+        // Act
+        var result = await store.GetBaselineAsync(DriftScope.Skill, "nonexistent", CancellationToken.None);
+
+        // Assert
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().BeNull();
+    }
+
+    [Fact]
+    public async Task SaveBaseline_OverwritesExisting()
+    {
+        // Arrange
+        _graphStoreMock
+            .Setup(g => g.AddNodesAsync(It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()))
+            .Returns(Task.CompletedTask);
+        _graphStoreMock
+            .Setup(g => g.AddEdgesAsync(It.IsAny<IReadOnlyList<GraphEdge>>(), It.IsAny<CancellationToken>()))
+            .Returns(Task.CompletedTask);
+
+        var store = CreateStore();
+
+        var v1 = CreateBaseline(DriftScope.TaskType, "summarization", sampleCount: 5);
+        var v2 = CreateBaseline(DriftScope.TaskType, "summarization", sampleCount: 20);
+
+        // Act
+        await store.SaveBaselineAsync(v1, CancellationToken.None);
+        var result = await store.SaveBaselineAsync(v2, CancellationToken.None);
+
+        // Assert — both saves use the same deterministic ID (upsert semantics)
+        result.IsSuccess.Should().BeTrue();
+        _graphStoreMock.Verify(g => g.AddNodesAsync(
+            It.Is<IReadOnlyList<GraphNode>>(nodes =>
+                nodes[0].Id == "driftbaseline:tasktype:summarization"),
+            It.IsAny<CancellationToken>()), Times.Exactly(2));
+    }
+
+    [Fact]
+    public async Task GetBaselines_ByScope_ReturnsFiltered()
+    {
+        // Arrange
+        var allNodes = new List<GraphNode>
+        {
+            CreateBaselineNode("driftbaseline:skill:code_review", DriftScope.Skill, "code_review"),
+            CreateBaselineNode("driftbaseline:skill:summarize", DriftScope.Skill, "summarize"),
+            CreateBaselineNode("driftbaseline:agent:agent-1", DriftScope.Agent, "agent-1")
+        };
+
+        _graphStoreMock
+            .Setup(g => g.GetAllNodesAsync(It.IsAny<CancellationToken>()))
+            .ReturnsAsync(allNodes.AsReadOnly());
+
+        var store = CreateStore();
+
+        // Act
+        var result = await store.GetBaselinesAsync(DriftScope.Skill, CancellationToken.None);
+
+        // Assert
+        result.IsSuccess.Should().BeTrue();
+        result.Value!.Should().HaveCount(2);
+        result.Value.Should().OnlyContain(b => b.Scope == DriftScope.Skill);
+    }
+
+    [Fact]
+    public async Task GetBaselines_NullScope_ReturnsAll()
+    {
+        // Arrange
+        var allNodes = new List<GraphNode>
+        {
+            CreateBaselineNode("driftbaseline:skill:code_review", DriftScope.Skill, "code_review"),
+            CreateBaselineNode("driftbaseline:agent:agent-1", DriftScope.Agent, "agent-1")
+        };
+
+        _graphStoreMock
+            .Setup(g => g.GetAllNodesAsync(It.IsAny<CancellationToken>()))
+            .ReturnsAsync(allNodes.AsReadOnly());
+
+        var store = CreateStore();
+
+        // Act
+        var result = await store.GetBaselinesAsync(null, CancellationToken.None);
+
+        // Assert
+        result.IsSuccess.Should().BeTrue();
+        result.Value!.Should().HaveCount(2);
+    }
+
+    [Fact]
+    public async Task SaveBaseline_CreatesEdge()
+    {
+        // Arrange
+        var baseline = CreateBaseline();
+        GraphEdge? capturedEdge = null;
+
+        _graphStoreMock
+            .Setup(g => g.AddNodesAsync(It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()))
+            .Returns(Task.CompletedTask);
+
+        _graphStoreMock
+            .Setup(g => g.AddEdgesAsync(It.IsAny<IReadOnlyList<GraphEdge>>(), It.IsAny<CancellationToken>()))
+            .Callback<IReadOnlyList<GraphEdge>, CancellationToken>((edges, _) => capturedEdge = edges[0])
+            .Returns(Task.CompletedTask);
+
+        var store = CreateStore();
+
+        // Act
+        await store.SaveBaselineAsync(baseline, CancellationToken.None);
+
+        // Assert
+        capturedEdge.Should().NotBeNull();
+        capturedEdge!.Predicate.Should().Be("baseline_for");
+        capturedEdge.SourceNodeId.Should().Be("driftbaseline:skill:code_review");
+        capturedEdge.TargetNodeId.Should().Be("scope:skill:code_review");
+    }
+
+    [Fact]
+    public async Task GetBaseline_GraphStoreThrows_ReturnsFailure()
+    {
+        // Arrange
+        _graphStoreMock
+            .Setup(g => g.GetNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
+            .ThrowsAsync(new InvalidOperationException("connection lost"));
+
+        var store = CreateStore();
+
+        // Act
+        var result = await store.GetBaselineAsync(DriftScope.Skill, "code_review", CancellationToken.None);
+
+        // Assert
+        result.IsSuccess.Should().BeFalse();
+        result.Errors.Should().ContainSingle().Which.Should().Contain("connection lost");
+    }
+
+    [Fact]
+    public async Task SaveBaseline_GraphStoreThrows_ReturnsFailure()
+    {
+        // Arrange
+        _graphStoreMock
+            .Setup(g => g.AddNodesAsync(It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()))
+            .ThrowsAsync(new InvalidOperationException("write failed"));
+
+        var store = CreateStore();
+
+        // Act
+        var result = await store.SaveBaselineAsync(CreateBaseline(), CancellationToken.None);
+
+        // Assert
+        result.IsSuccess.Should().BeFalse();
+        result.Errors.Should().ContainSingle().Which.Should().Contain("write failed");
+    }
+
+    private static GraphNode CreateBaselineNode(string id, DriftScope scope, string identifier) => new()
+    {
+        Id = id,
+        Name = $"DriftBaseline:{scope}:{identifier}",
+        Type = "DriftBaseline",
+        Properties = new Dictionary<string, string>
+        {
+            ["BaselineId"] = Guid.NewGuid().ToString(),
+            ["Scope"] = scope.ToString(),
+            ["ScopeIdentifier"] = identifier,
+            ["Dimensions"] = "{\"Faithfulness\":0.85,\"Relevance\":0.9}",
+            ["DimensionSigmas"] = "{\"Faithfulness\":0.05,\"Relevance\":0.03}",
+            ["SampleCount"] = "10",
+            ["WindowStart"] = "2026-05-01T00:00:00+00:00",
+            ["WindowEnd"] = "2026-05-10T00:00:00+00:00",
+            ["CreatedAt"] = "2026-05-10T12:00:00+00:00"
+        }.AsReadOnly()
+    };
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/InMemoryDriftBaselineStoreTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/InMemoryDriftBaselineStoreTests.cs
new file mode 100644
index 0000000..a9d2e00
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/InMemoryDriftBaselineStoreTests.cs
@@ -0,0 +1,122 @@
+using Domain.AI.DriftDetection;
+using FluentAssertions;
+using Infrastructure.AI.DriftDetection;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.DriftDetection;
+
+public sealed class InMemoryDriftBaselineStoreTests
+{
+    private static DriftBaseline CreateBaseline(
+        DriftScope scope = DriftScope.Skill,
+        string scopeIdentifier = "code_review",
+        int sampleCount = 10) => new()
+    {
+        BaselineId = Guid.NewGuid(),
+        Scope = scope,
+        ScopeIdentifier = scopeIdentifier,
+        Dimensions = new Dictionary<DriftDimension, double>
+        {
+            [DriftDimension.Faithfulness] = 0.85,
+            [DriftDimension.Relevance] = 0.90
+        }.AsReadOnly(),
+        DimensionSigmas = new Dictionary<DriftDimension, double>
+        {
+            [DriftDimension.Faithfulness] = 0.05,
+            [DriftDimension.Relevance] = 0.03
+        }.AsReadOnly(),
+        SampleCount = sampleCount,
+        WindowStart = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
+        WindowEnd = new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero),
+        CreatedAt = new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero)
+    };
+
+    [Fact]
+    public async Task SaveAndRetrieve_RoundTrips()
+    {
+        // Arrange
+        var store = new InMemoryDriftBaselineStore();
+        var baseline = CreateBaseline();
+
+        // Act
+        await store.SaveBaselineAsync(baseline, CancellationToken.None);
+        var result = await store.GetBaselineAsync(DriftScope.Skill, "code_review", CancellationToken.None);
+
+        // Assert
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().NotBeNull();
+        result.Value!.BaselineId.Should().Be(baseline.BaselineId);
+        result.Value.Scope.Should().Be(DriftScope.Skill);
+        result.Value.ScopeIdentifier.Should().Be("code_review");
+        result.Value.Dimensions.Should().BeEquivalentTo(baseline.Dimensions);
+        result.Value.DimensionSigmas.Should().BeEquivalentTo(baseline.DimensionSigmas);
+        result.Value.SampleCount.Should().Be(10);
+    }
+
+    [Fact]
+    public async Task OverwriteExisting_ReplacesValue()
+    {
+        // Arrange
+        var store = new InMemoryDriftBaselineStore();
+        var v1 = CreateBaseline(sampleCount: 5);
+        var v2 = CreateBaseline(sampleCount: 20);
+
+        // Act
+        await store.SaveBaselineAsync(v1, CancellationToken.None);
+        await store.SaveBaselineAsync(v2, CancellationToken.None);
+        var result = await store.GetBaselineAsync(DriftScope.Skill, "code_review", CancellationToken.None);
+
+        // Assert
+        result.IsSuccess.Should().BeTrue();
+        result.Value!.SampleCount.Should().Be(20);
+    }
+
+    [Fact]
+    public async Task GetBaselines_FiltersByScope()
+    {
+        // Arrange
+        var store = new InMemoryDriftBaselineStore();
+        await store.SaveBaselineAsync(CreateBaseline(DriftScope.Skill, "a"), CancellationToken.None);
+        await store.SaveBaselineAsync(CreateBaseline(DriftScope.Skill, "b"), CancellationToken.None);
+        await store.SaveBaselineAsync(CreateBaseline(DriftScope.Agent, "agent-1"), CancellationToken.None);
+
+        // Act
+        var result = await store.GetBaselinesAsync(DriftScope.Skill, CancellationToken.None);
+
+        // Assert
+        result.IsSuccess.Should().BeTrue();
+        result.Value!.Should().HaveCount(2);
+        result.Value.Should().OnlyContain(b => b.Scope == DriftScope.Skill);
+    }
+
+    [Fact]
+    public async Task GetBaseline_NotFound_ReturnsNull()
+    {
+        // Arrange
+        var store = new InMemoryDriftBaselineStore();
+
+        // Act
+        var result = await store.GetBaselineAsync(DriftScope.Skill, "nonexistent", CancellationToken.None);
+
+        // Assert
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().BeNull();
+    }
+
+    [Fact]
+    public async Task GetBaselines_NullScope_ReturnsAll()
+    {
+        // Arrange
+        var store = new InMemoryDriftBaselineStore();
+        await store.SaveBaselineAsync(CreateBaseline(DriftScope.Skill, "a"), CancellationToken.None);
+        await store.SaveBaselineAsync(CreateBaseline(DriftScope.Agent, "agent-1"), CancellationToken.None);
+        await store.SaveBaselineAsync(CreateBaseline(DriftScope.TaskType, "summarization"), CancellationToken.None);
+
+        // Act
+        var result = await store.GetBaselinesAsync(null, CancellationToken.None);
+
+        // Assert
+        result.IsSuccess.Should().BeTrue();
+        result.Value!.Should().HaveCount(3);
+    }
+}

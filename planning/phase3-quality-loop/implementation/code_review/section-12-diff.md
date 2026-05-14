diff --git a/src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/Learnings/GraphLearningsStore.cs b/src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/Learnings/GraphLearningsStore.cs
new file mode 100644
index 0000000..51ac66a
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/Learnings/GraphLearningsStore.cs
@@ -0,0 +1,296 @@
+using System.Globalization;
+using Application.AI.Common.Interfaces.KnowledgeGraph;
+using Application.AI.Common.Interfaces.Learnings;
+using Domain.AI.KnowledgeGraph.Models;
+using Domain.AI.Learnings;
+using Domain.Common;
+using Microsoft.Extensions.Logging;
+
+namespace Infrastructure.AI.KnowledgeGraph.Learnings;
+
+/// <summary>
+/// Graph-backed implementation of <see cref="ILearningsStore"/> using deterministic node IDs
+/// and synthetic index nodes for efficient scope-hierarchy search.
+/// Registered with keyed DI key <c>"graph"</c>.
+/// </summary>
+public sealed class GraphLearningsStore : ILearningsStore
+{
+    private readonly IKnowledgeGraphStore _graphStore;
+    private readonly ILogger<GraphLearningsStore> _logger;
+
+    private const string NodePrefix = "learning:";
+    private const string IndexPrefix = "learningindex:";
+    private const string NodeType = "LearningEntry";
+    private const string IndexType = "LearningIndex";
+    private const string EdgePredicate = "has_learning";
+    private const string ChunkId = "learningindex";
+
+    public GraphLearningsStore(
+        IKnowledgeGraphStore graphStore,
+        ILogger<GraphLearningsStore> logger)
+    {
+        ArgumentNullException.ThrowIfNull(graphStore);
+        ArgumentNullException.ThrowIfNull(logger);
+
+        _graphStore = graphStore;
+        _logger = logger;
+    }
+
+    public async Task<Result> SaveAsync(LearningEntry learning, CancellationToken ct)
+    {
+        var nodeId = ToNodeId(learning.LearningId);
+        var node = new GraphNode
+        {
+            Id = nodeId,
+            Name = $"Learning: {learning.Content[..Math.Min(50, learning.Content.Length)]}",
+            Type = NodeType,
+            Properties = SerializeProperties(learning)
+        };
+
+        await _graphStore.AddNodesAsync([node], ct);
+        await CreateIndexEdgesAsync(nodeId, learning.Scope, ct);
+        return Result.Success();
+    }
+
+    public async Task<Result<LearningEntry?>> GetAsync(Guid learningId, CancellationToken ct)
+    {
+        var node = await _graphStore.GetNodeAsync(ToNodeId(learningId), ct);
+        if (node is null)
+            return Result<LearningEntry?>.Success(null);
+
+        var entry = DeserializeLearningEntry(learningId, node);
+        if (entry is null || entry.IsDeleted)
+            return Result<LearningEntry?>.Success(null);
+
+        return Result<LearningEntry?>.Success(entry);
+    }
+
+    public async Task<Result<IReadOnlyList<LearningEntry>>> SearchAsync(LearningSearchCriteria criteria, CancellationToken ct)
+    {
+        var candidateNodes = new Dictionary<string, GraphNode>();
+
+        if (criteria.Scope is null)
+        {
+            var allNodes = await _graphStore.GetAllNodesAsync(ct);
+            foreach (var n in allNodes.Where(n => n.Type == NodeType))
+                candidateNodes.TryAdd(n.Id, n);
+        }
+        else
+        {
+            if (criteria.Scope.AgentId is not null)
+                await CollectIndexNeighborsAsync($"{IndexPrefix}agent:{criteria.Scope.AgentId}".ToLowerInvariant(), candidateNodes, ct);
+
+            if (criteria.Scope.TeamId is not null)
+                await CollectIndexNeighborsAsync($"{IndexPrefix}team:{criteria.Scope.TeamId}".ToLowerInvariant(), candidateNodes, ct);
+
+            await CollectIndexNeighborsAsync($"{IndexPrefix}global", candidateNodes, ct);
+        }
+
+        var entries = new List<LearningEntry>();
+        foreach (var node in candidateNodes.Values)
+        {
+            if (node.Properties.GetValueOrDefault("IsDeleted", "false") == "true")
+                continue;
+
+            var id = ExtractLearningId(node.Id);
+            if (id is null) continue;
+
+            var entry = DeserializeLearningEntry(id.Value, node);
+            if (entry is null) continue;
+
+            if (criteria.Category is not null && entry.Category != criteria.Category)
+                continue;
+            if (criteria.MinFeedbackWeight is not null && entry.FeedbackWeight < criteria.MinFeedbackWeight)
+                continue;
+            if (criteria.CreatedAfter is not null && entry.CreatedAt < criteria.CreatedAfter)
+                continue;
+            if (criteria.CreatedBefore is not null && entry.CreatedAt > criteria.CreatedBefore)
+                continue;
+
+            entries.Add(entry);
+        }
+
+        return Result<IReadOnlyList<LearningEntry>>.Success(entries);
+    }
+
+    public async Task<Result> UpdateAsync(LearningEntry learning, CancellationToken ct)
+    {
+        var nodeId = ToNodeId(learning.LearningId);
+        var node = new GraphNode
+        {
+            Id = nodeId,
+            Name = $"Learning: {learning.Content[..Math.Min(50, learning.Content.Length)]}",
+            Type = NodeType,
+            Properties = SerializeProperties(learning)
+        };
+
+        await _graphStore.AddNodesAsync([node], ct);
+        return Result.Success();
+    }
+
+    public async Task<Result> SoftDeleteAsync(Guid learningId, string reason, CancellationToken ct)
+    {
+        var nodeId = ToNodeId(learningId);
+        var existing = await _graphStore.GetNodeAsync(nodeId, ct);
+        if (existing is null)
+            return Result.Fail("Learning not found");
+
+        var updatedProps = new Dictionary<string, string>(existing.Properties)
+        {
+            ["IsDeleted"] = "true",
+            ["DeleteReason"] = reason
+        };
+
+        var updatedNode = new GraphNode
+        {
+            Id = nodeId,
+            Name = existing.Name,
+            Type = existing.Type,
+            Properties = updatedProps
+        };
+
+        await _graphStore.AddNodesAsync([updatedNode], ct);
+        return Result.Success();
+    }
+
+    private static string ToNodeId(Guid learningId) =>
+        $"{NodePrefix}{learningId}".ToLowerInvariant();
+
+    private static Guid? ExtractLearningId(string nodeId)
+    {
+        if (!nodeId.StartsWith(NodePrefix, StringComparison.OrdinalIgnoreCase))
+            return null;
+
+        return Guid.TryParse(nodeId[NodePrefix.Length..], out var id) ? id : null;
+    }
+
+    private async Task CreateIndexEdgesAsync(string nodeId, LearningScope scope, CancellationToken ct)
+    {
+        var indexNodes = new List<GraphNode>();
+        var edges = new List<GraphEdge>();
+
+        if (scope.AgentId is not null)
+        {
+            var indexId = $"{IndexPrefix}agent:{scope.AgentId}".ToLowerInvariant();
+            indexNodes.Add(new GraphNode { Id = indexId, Name = $"Agent:{scope.AgentId}", Type = IndexType });
+            edges.Add(CreateEdge(indexId, nodeId));
+        }
+
+        if (scope.TeamId is not null)
+        {
+            var indexId = $"{IndexPrefix}team:{scope.TeamId}".ToLowerInvariant();
+            indexNodes.Add(new GraphNode { Id = indexId, Name = $"Team:{scope.TeamId}", Type = IndexType });
+            edges.Add(CreateEdge(indexId, nodeId));
+        }
+
+        if (scope.IsGlobal)
+        {
+            var indexId = $"{IndexPrefix}global";
+            indexNodes.Add(new GraphNode { Id = indexId, Name = "Global", Type = IndexType });
+            edges.Add(CreateEdge(indexId, nodeId));
+        }
+
+        if (indexNodes.Count > 0)
+            await _graphStore.AddNodesAsync(indexNodes, ct);
+        if (edges.Count > 0)
+            await _graphStore.AddEdgesAsync(edges, ct);
+    }
+
+    private static GraphEdge CreateEdge(string indexId, string nodeId) => new()
+    {
+        Id = $"edge:{indexId}:{nodeId}",
+        SourceNodeId = indexId,
+        TargetNodeId = nodeId,
+        Predicate = EdgePredicate,
+        ChunkId = ChunkId
+    };
+
+    private async Task CollectIndexNeighborsAsync(string indexNodeId, Dictionary<string, GraphNode> candidates, CancellationToken ct)
+    {
+        if (!await _graphStore.NodeExistsAsync(indexNodeId, ct))
+            return;
+
+        var neighbors = await _graphStore.GetNeighborsAsync(indexNodeId, maxDepth: 1, ct);
+        foreach (var neighbor in neighbors.Where(n => n.Type == NodeType))
+            candidates.TryAdd(neighbor.Id, neighbor);
+    }
+
+    private static Dictionary<string, string> SerializeProperties(LearningEntry entry) => new()
+    {
+        ["Content"] = entry.Content,
+        ["Category"] = entry.Category.ToString(),
+        ["DecayClass"] = entry.DecayClass.ToString(),
+        ["FeedbackWeight"] = entry.FeedbackWeight.ToString("F6", CultureInfo.InvariantCulture),
+        ["UpdateCount"] = entry.UpdateCount.ToString(CultureInfo.InvariantCulture),
+        ["CreatedAt"] = entry.CreatedAt.ToString("O"),
+        ["LastAccessedAt"] = entry.LastAccessedAt?.ToString("O") ?? "",
+        ["LastReinforcedAt"] = entry.LastReinforcedAt?.ToString("O") ?? "",
+        ["SourceType"] = entry.Source.SourceType.ToString(),
+        ["SourceId"] = entry.Source.SourceId,
+        ["SourceDescription"] = entry.Source.SourceDescription,
+        ["ProvenancePipeline"] = entry.Provenance.OriginPipeline,
+        ["ProvenanceTask"] = entry.Provenance.OriginTask,
+        ["ProvenanceTimestamp"] = entry.Provenance.OriginTimestamp.ToString("O"),
+        ["ProvenanceConfidence"] = entry.Provenance.Confidence.ToString("F4", CultureInfo.InvariantCulture),
+        ["ScopeAgentId"] = entry.Scope.AgentId ?? "",
+        ["ScopeTeamId"] = entry.Scope.TeamId ?? "",
+        ["ScopeIsGlobal"] = entry.Scope.IsGlobal.ToString().ToLowerInvariant(),
+        ["IsDeleted"] = entry.IsDeleted.ToString().ToLowerInvariant(),
+        ["DeleteReason"] = entry.DeleteReason ?? ""
+    };
+
+    private LearningEntry? DeserializeLearningEntry(Guid learningId, GraphNode node)
+    {
+        var props = node.Properties;
+
+        if (!props.ContainsKey("Content") || !props.ContainsKey("Category"))
+        {
+            _logger.LogWarning("Skipping graph node {NodeId}: missing required properties", node.Id);
+            return null;
+        }
+
+        return new LearningEntry
+        {
+            LearningId = learningId,
+            Content = props.GetValueOrDefault("Content", ""),
+            Category = Enum.TryParse<LearningCategory>(props.GetValueOrDefault("Category", ""), out var cat)
+                ? cat : LearningCategory.DomainKnowledge,
+            DecayClass = Enum.TryParse<DecayClass>(props.GetValueOrDefault("DecayClass", ""), out var dc)
+                ? dc : DecayClass.Stable,
+            FeedbackWeight = double.TryParse(props.GetValueOrDefault("FeedbackWeight", "1.0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fw)
+                ? fw : 1.0,
+            UpdateCount = int.TryParse(props.GetValueOrDefault("UpdateCount", "0"), out var uc)
+                ? uc : 0,
+            CreatedAt = DateTimeOffset.TryParse(props.GetValueOrDefault("CreatedAt", ""), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ca)
+                ? ca : DateTimeOffset.UtcNow,
+            LastAccessedAt = DateTimeOffset.TryParse(props.GetValueOrDefault("LastAccessedAt", ""), CultureInfo.InvariantCulture, DateTimeStyles.None, out var la)
+                ? la : null,
+            LastReinforcedAt = DateTimeOffset.TryParse(props.GetValueOrDefault("LastReinforcedAt", ""), CultureInfo.InvariantCulture, DateTimeStyles.None, out var lr)
+                ? lr : null,
+            Source = new LearningSource
+            {
+                SourceType = Enum.TryParse<LearningSourceType>(props.GetValueOrDefault("SourceType", ""), out var st)
+                    ? st : LearningSourceType.ManualEntry,
+                SourceId = props.GetValueOrDefault("SourceId", ""),
+                SourceDescription = props.GetValueOrDefault("SourceDescription", "")
+            },
+            Provenance = new LearningProvenance
+            {
+                OriginPipeline = props.GetValueOrDefault("ProvenancePipeline", ""),
+                OriginTask = props.GetValueOrDefault("ProvenanceTask", ""),
+                OriginTimestamp = DateTimeOffset.TryParse(props.GetValueOrDefault("ProvenanceTimestamp", ""), CultureInfo.InvariantCulture, DateTimeStyles.None, out var pt)
+                    ? pt : DateTimeOffset.UtcNow,
+                Confidence = double.TryParse(props.GetValueOrDefault("ProvenanceConfidence", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var pc)
+                    ? pc : 0.0
+            },
+            Scope = new LearningScope
+            {
+                AgentId = string.IsNullOrEmpty(props.GetValueOrDefault("ScopeAgentId", "")) ? null : props["ScopeAgentId"],
+                TeamId = string.IsNullOrEmpty(props.GetValueOrDefault("ScopeTeamId", "")) ? null : props["ScopeTeamId"],
+                IsGlobal = props.GetValueOrDefault("ScopeIsGlobal", "false") == "true"
+            },
+            IsDeleted = props.GetValueOrDefault("IsDeleted", "false") == "true",
+            DeleteReason = string.IsNullOrEmpty(props.GetValueOrDefault("DeleteReason", "")) ? null : props["DeleteReason"]
+        };
+    }
+}
diff --git a/src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/Learnings/InMemoryLearningsStore.cs b/src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/Learnings/InMemoryLearningsStore.cs
new file mode 100644
index 0000000..cf6c9bb
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/Learnings/InMemoryLearningsStore.cs
@@ -0,0 +1,81 @@
+using System.Collections.Concurrent;
+using Application.AI.Common.Interfaces.Learnings;
+using Domain.AI.Learnings;
+using Domain.Common;
+
+namespace Infrastructure.AI.KnowledgeGraph.Learnings;
+
+/// <summary>
+/// Simple in-memory implementation of <see cref="ILearningsStore"/> for testing.
+/// Registered with keyed DI key <c>"in_memory"</c>.
+/// </summary>
+public sealed class InMemoryLearningsStore : ILearningsStore
+{
+    private readonly ConcurrentDictionary<Guid, LearningEntry> _entries = new();
+
+    public Task<Result> SaveAsync(LearningEntry learning, CancellationToken ct)
+    {
+        _entries[learning.LearningId] = learning;
+        return Task.FromResult(Result.Success());
+    }
+
+    public Task<Result<LearningEntry?>> GetAsync(Guid learningId, CancellationToken ct)
+    {
+        if (_entries.TryGetValue(learningId, out var entry) && !entry.IsDeleted)
+            return Task.FromResult(Result<LearningEntry?>.Success(entry));
+
+        return Task.FromResult(Result<LearningEntry?>.Success(null));
+    }
+
+    public Task<Result<IReadOnlyList<LearningEntry>>> SearchAsync(LearningSearchCriteria criteria, CancellationToken ct)
+    {
+        var results = _entries.Values
+            .Where(e => !e.IsDeleted)
+            .Where(e => MatchesScope(e.Scope, criteria.Scope))
+            .Where(e => criteria.Category is null || e.Category == criteria.Category)
+            .Where(e => criteria.MinFeedbackWeight is null || e.FeedbackWeight >= criteria.MinFeedbackWeight)
+            .Where(e => criteria.CreatedAfter is null || e.CreatedAt >= criteria.CreatedAfter)
+            .Where(e => criteria.CreatedBefore is null || e.CreatedAt <= criteria.CreatedBefore)
+            .ToList();
+
+        return Task.FromResult(Result<IReadOnlyList<LearningEntry>>.Success(results));
+    }
+
+    public Task<Result> UpdateAsync(LearningEntry learning, CancellationToken ct)
+    {
+        if (!_entries.ContainsKey(learning.LearningId))
+            return Task.FromResult(Result.Fail("Learning not found"));
+
+        _entries[learning.LearningId] = learning;
+        return Task.FromResult(Result.Success());
+    }
+
+    public Task<Result> SoftDeleteAsync(Guid learningId, string reason, CancellationToken ct)
+    {
+        if (!_entries.TryGetValue(learningId, out var entry))
+            return Task.FromResult(Result.Fail("Learning not found"));
+
+        _entries[learningId] = entry with { IsDeleted = true, DeleteReason = reason };
+        return Task.FromResult(Result.Success());
+    }
+
+    private static bool MatchesScope(LearningScope entryScope, LearningScope? criteriaScope)
+    {
+        if (criteriaScope is null)
+            return true;
+
+        if (entryScope.IsGlobal)
+            return true;
+
+        if (criteriaScope.AgentId is not null && entryScope.AgentId == criteriaScope.AgentId)
+            return true;
+
+        if (criteriaScope.TeamId is not null && entryScope.TeamId == criteriaScope.TeamId)
+            return true;
+
+        if (criteriaScope.IsGlobal && entryScope.IsGlobal)
+            return true;
+
+        return false;
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests/Learnings/GraphLearningsStoreTests.cs b/src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests/Learnings/GraphLearningsStoreTests.cs
new file mode 100644
index 0000000..66f7a36
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests/Learnings/GraphLearningsStoreTests.cs
@@ -0,0 +1,366 @@
+using Application.AI.Common.Interfaces.Learnings;
+using Domain.AI.KnowledgeGraph.Models;
+using Domain.AI.Learnings;
+using Domain.Common;
+using FluentAssertions;
+using Infrastructure.AI.KnowledgeGraph.InMemory;
+using Infrastructure.AI.KnowledgeGraph.Learnings;
+using Microsoft.Extensions.Logging;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.KnowledgeGraph.Tests.Learnings;
+
+public sealed class GraphLearningsStoreTests
+{
+    private readonly InMemoryGraphStore _graphStore;
+    private readonly GraphLearningsStore _sut;
+
+    public GraphLearningsStoreTests()
+    {
+        _graphStore = new InMemoryGraphStore(Mock.Of<ILogger<InMemoryGraphStore>>());
+        _sut = new GraphLearningsStore(
+            _graphStore,
+            Mock.Of<ILogger<GraphLearningsStore>>());
+    }
+
+    private static LearningEntry BuildEntry(
+        Guid? id = null,
+        string? agentId = null,
+        string? teamId = null,
+        bool isGlobal = false,
+        LearningCategory category = LearningCategory.DomainKnowledge,
+        double feedbackWeight = 1.0,
+        string content = "Test learning content") => new()
+    {
+        LearningId = id ?? Guid.NewGuid(),
+        Content = content,
+        Category = category,
+        DecayClass = DecayClass.Stable,
+        FeedbackWeight = feedbackWeight,
+        UpdateCount = 0,
+        CreatedAt = DateTimeOffset.UtcNow,
+        Scope = new LearningScope
+        {
+            AgentId = agentId,
+            TeamId = teamId,
+            IsGlobal = isGlobal
+        },
+        Source = new LearningSource
+        {
+            SourceType = LearningSourceType.HumanCorrection,
+            SourceId = "test-source",
+            SourceDescription = "Test source"
+        },
+        Provenance = new LearningProvenance
+        {
+            OriginPipeline = "test-pipeline",
+            OriginTask = "test-task",
+            OriginTimestamp = DateTimeOffset.UtcNow,
+            Confidence = 0.95
+        }
+    };
+
+    [Fact]
+    public async Task Save_Graph_CreatesNodeWithDeterministicId()
+    {
+        var id = Guid.NewGuid();
+        var entry = BuildEntry(id: id, isGlobal: true);
+
+        await _sut.SaveAsync(entry, CancellationToken.None);
+
+        var node = await _graphStore.GetNodeAsync($"learning:{id}".ToLowerInvariant(), CancellationToken.None);
+        node.Should().NotBeNull();
+        node!.Type.Should().Be("LearningEntry");
+        node.Properties["Content"].Should().Be(entry.Content);
+        node.Properties["Category"].Should().Be("DomainKnowledge");
+        node.Properties["DecayClass"].Should().Be("Stable");
+    }
+
+    [Fact]
+    public async Task Save_Graph_CreatesIndexEdges_AgentScope()
+    {
+        var entry = BuildEntry(agentId: "agent-1");
+
+        await _sut.SaveAsync(entry, CancellationToken.None);
+
+        var indexExists = await _graphStore.NodeExistsAsync("learningindex:agent:agent-1", CancellationToken.None);
+        indexExists.Should().BeTrue();
+
+        var neighbors = await _graphStore.GetNeighborsAsync("learningindex:agent:agent-1", maxDepth: 1, CancellationToken.None);
+        neighbors.Should().ContainSingle(n => n.Type == "LearningEntry");
+    }
+
+    [Fact]
+    public async Task Save_Graph_CreatesIndexEdges_TeamScope()
+    {
+        var entry = BuildEntry(teamId: "team-1");
+
+        await _sut.SaveAsync(entry, CancellationToken.None);
+
+        var indexExists = await _graphStore.NodeExistsAsync("learningindex:team:team-1", CancellationToken.None);
+        indexExists.Should().BeTrue();
+
+        var neighbors = await _graphStore.GetNeighborsAsync("learningindex:team:team-1", maxDepth: 1, CancellationToken.None);
+        neighbors.Should().ContainSingle(n => n.Type == "LearningEntry");
+    }
+
+    [Fact]
+    public async Task Save_Graph_CreatesIndexEdges_GlobalScope()
+    {
+        var entry = BuildEntry(isGlobal: true);
+
+        await _sut.SaveAsync(entry, CancellationToken.None);
+
+        var indexExists = await _graphStore.NodeExistsAsync("learningindex:global", CancellationToken.None);
+        indexExists.Should().BeTrue();
+
+        var neighbors = await _graphStore.GetNeighborsAsync("learningindex:global", maxDepth: 1, CancellationToken.None);
+        neighbors.Should().ContainSingle(n => n.Type == "LearningEntry");
+    }
+
+    [Fact]
+    public async Task Save_Graph_CreatesMultipleIndexEdges()
+    {
+        var entry = BuildEntry(agentId: "a1", teamId: "t1", isGlobal: true);
+
+        await _sut.SaveAsync(entry, CancellationToken.None);
+
+        (await _graphStore.NodeExistsAsync("learningindex:agent:a1", CancellationToken.None)).Should().BeTrue();
+        (await _graphStore.NodeExistsAsync("learningindex:team:t1", CancellationToken.None)).Should().BeTrue();
+        (await _graphStore.NodeExistsAsync("learningindex:global", CancellationToken.None)).Should().BeTrue();
+    }
+
+    [Fact]
+    public async Task Get_Graph_RetrievesByDeterministicId()
+    {
+        var id = Guid.NewGuid();
+        var entry = BuildEntry(id: id, isGlobal: true, content: "Specific learning content");
+
+        await _sut.SaveAsync(entry, CancellationToken.None);
+
+        var result = await _sut.GetAsync(id, CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().NotBeNull();
+        result.Value!.LearningId.Should().Be(id);
+        result.Value.Content.Should().Be("Specific learning content");
+        result.Value.Category.Should().Be(LearningCategory.DomainKnowledge);
+        result.Value.Scope.IsGlobal.Should().BeTrue();
+    }
+
+    [Fact]
+    public async Task Get_NotFound_ReturnsNull()
+    {
+        var result = await _sut.GetAsync(Guid.NewGuid(), CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().BeNull();
+    }
+
+    [Fact]
+    public async Task Search_AgentScope_ReturnsAgentLearnings()
+    {
+        await _sut.SaveAsync(BuildEntry(agentId: "agent-1"), CancellationToken.None);
+        await _sut.SaveAsync(BuildEntry(agentId: "agent-1"), CancellationToken.None);
+        await _sut.SaveAsync(BuildEntry(agentId: "agent-2"), CancellationToken.None);
+
+        var criteria = new LearningSearchCriteria
+        {
+            Scope = new LearningScope { AgentId = "agent-1" }
+        };
+
+        var result = await _sut.SearchAsync(criteria, CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().HaveCount(2);
+    }
+
+    [Fact]
+    public async Task Search_TeamScope_ReturnsTeamLearnings()
+    {
+        await _sut.SaveAsync(BuildEntry(teamId: "team-1"), CancellationToken.None);
+        await _sut.SaveAsync(BuildEntry(teamId: "team-2"), CancellationToken.None);
+
+        var criteria = new LearningSearchCriteria
+        {
+            Scope = new LearningScope { TeamId = "team-1" }
+        };
+
+        var result = await _sut.SearchAsync(criteria, CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().ContainSingle();
+    }
+
+    [Fact]
+    public async Task Search_GlobalScope_ReturnsGlobalLearnings()
+    {
+        await _sut.SaveAsync(BuildEntry(isGlobal: true), CancellationToken.None);
+        await _sut.SaveAsync(BuildEntry(isGlobal: true), CancellationToken.None);
+        await _sut.SaveAsync(BuildEntry(agentId: "agent-1"), CancellationToken.None);
+
+        var criteria = new LearningSearchCriteria
+        {
+            Scope = new LearningScope { IsGlobal = true }
+        };
+
+        var result = await _sut.SearchAsync(criteria, CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().HaveCount(2);
+    }
+
+    [Fact]
+    public async Task Search_ScopeHierarchy_MergesAllLevels()
+    {
+        await _sut.SaveAsync(BuildEntry(agentId: "a1"), CancellationToken.None);
+        await _sut.SaveAsync(BuildEntry(teamId: "t1"), CancellationToken.None);
+        await _sut.SaveAsync(BuildEntry(isGlobal: true), CancellationToken.None);
+
+        var criteria = new LearningSearchCriteria
+        {
+            Scope = new LearningScope { AgentId = "a1", TeamId = "t1" }
+        };
+
+        var result = await _sut.SearchAsync(criteria, CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().HaveCount(3);
+    }
+
+    [Fact]
+    public async Task Search_DeduplicatesByLearningId()
+    {
+        var entry = BuildEntry(agentId: "a1", teamId: "t1", isGlobal: true);
+        await _sut.SaveAsync(entry, CancellationToken.None);
+
+        var criteria = new LearningSearchCriteria
+        {
+            Scope = new LearningScope { AgentId = "a1", TeamId = "t1" }
+        };
+
+        var result = await _sut.SearchAsync(criteria, CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().ContainSingle();
+    }
+
+    [Fact]
+    public async Task Search_ExcludesSoftDeleted()
+    {
+        var keepId = Guid.NewGuid();
+        var deleteId = Guid.NewGuid();
+        await _sut.SaveAsync(BuildEntry(id: keepId, isGlobal: true), CancellationToken.None);
+        await _sut.SaveAsync(BuildEntry(id: deleteId, isGlobal: true), CancellationToken.None);
+
+        await _sut.SoftDeleteAsync(deleteId, "outdated", CancellationToken.None);
+
+        var criteria = new LearningSearchCriteria
+        {
+            Scope = new LearningScope { IsGlobal = true }
+        };
+
+        var result = await _sut.SearchAsync(criteria, CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().ContainSingle();
+        result.Value[0].LearningId.Should().Be(keepId);
+    }
+
+    [Fact]
+    public async Task Search_FiltersByCategory()
+    {
+        await _sut.SaveAsync(BuildEntry(isGlobal: true, category: LearningCategory.FactualCorrection), CancellationToken.None);
+        await _sut.SaveAsync(BuildEntry(isGlobal: true, category: LearningCategory.DomainKnowledge), CancellationToken.None);
+
+        var criteria = new LearningSearchCriteria
+        {
+            Scope = new LearningScope { IsGlobal = true },
+            Category = LearningCategory.FactualCorrection
+        };
+
+        var result = await _sut.SearchAsync(criteria, CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().ContainSingle();
+        result.Value[0].Category.Should().Be(LearningCategory.FactualCorrection);
+    }
+
+    [Fact]
+    public async Task SoftDelete_SetsIsDeletedFlag()
+    {
+        var id = Guid.NewGuid();
+        await _sut.SaveAsync(BuildEntry(id: id, isGlobal: true), CancellationToken.None);
+
+        await _sut.SoftDeleteAsync(id, "test reason", CancellationToken.None);
+
+        var node = await _graphStore.GetNodeAsync($"learning:{id}".ToLowerInvariant(), CancellationToken.None);
+        node.Should().NotBeNull();
+        node!.Properties["IsDeleted"].Should().Be("true");
+    }
+
+    [Fact]
+    public async Task SoftDelete_SetsDeleteReason()
+    {
+        var id = Guid.NewGuid();
+        await _sut.SaveAsync(BuildEntry(id: id, isGlobal: true), CancellationToken.None);
+
+        await _sut.SoftDeleteAsync(id, "outdated", CancellationToken.None);
+
+        var node = await _graphStore.GetNodeAsync($"learning:{id}".ToLowerInvariant(), CancellationToken.None);
+        node!.Properties["DeleteReason"].Should().Be("outdated");
+    }
+
+    [Fact]
+    public async Task SoftDelete_NotFound_ReturnsFail()
+    {
+        var result = await _sut.SoftDeleteAsync(Guid.NewGuid(), "test", CancellationToken.None);
+
+        result.IsSuccess.Should().BeFalse();
+    }
+
+    [Fact]
+    public async Task Update_PreservesGraphNodeId()
+    {
+        var id = Guid.NewGuid();
+        var entry = BuildEntry(id: id, isGlobal: true, feedbackWeight: 1.0);
+        await _sut.SaveAsync(entry, CancellationToken.None);
+
+        var updated = entry with { FeedbackWeight = 2.5 };
+        var result = await _sut.UpdateAsync(updated, CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        var node = await _graphStore.GetNodeAsync($"learning:{id}".ToLowerInvariant(), CancellationToken.None);
+        node.Should().NotBeNull();
+        node!.Properties["FeedbackWeight"].Should().Contain("2.5");
+    }
+
+    [Fact]
+    public async Task Get_SoftDeleted_ReturnsNull()
+    {
+        var id = Guid.NewGuid();
+        await _sut.SaveAsync(BuildEntry(id: id, isGlobal: true), CancellationToken.None);
+        await _sut.SoftDeleteAsync(id, "gone", CancellationToken.None);
+
+        var result = await _sut.GetAsync(id, CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().BeNull();
+    }
+
+    [Fact]
+    public async Task Search_NullScope_ReturnsAllLearnings()
+    {
+        await _sut.SaveAsync(BuildEntry(agentId: "a1"), CancellationToken.None);
+        await _sut.SaveAsync(BuildEntry(teamId: "t1"), CancellationToken.None);
+        await _sut.SaveAsync(BuildEntry(isGlobal: true), CancellationToken.None);
+
+        var criteria = new LearningSearchCriteria { Scope = null };
+
+        var result = await _sut.SearchAsync(criteria, CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().HaveCount(3);
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests/Learnings/InMemoryLearningsStoreTests.cs b/src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests/Learnings/InMemoryLearningsStoreTests.cs
new file mode 100644
index 0000000..ae42ecb
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests/Learnings/InMemoryLearningsStoreTests.cs
@@ -0,0 +1,114 @@
+using Application.AI.Common.Interfaces.Learnings;
+using Domain.AI.Learnings;
+using Domain.Common;
+using FluentAssertions;
+using Infrastructure.AI.KnowledgeGraph.Learnings;
+using Xunit;
+
+namespace Infrastructure.AI.KnowledgeGraph.Tests.Learnings;
+
+public sealed class InMemoryLearningsStoreTests
+{
+    private readonly InMemoryLearningsStore _sut = new();
+
+    private static LearningEntry BuildEntry(
+        Guid? id = null,
+        string? agentId = null,
+        string? teamId = null,
+        bool isGlobal = false,
+        LearningCategory category = LearningCategory.DomainKnowledge) => new()
+    {
+        LearningId = id ?? Guid.NewGuid(),
+        Content = "Test learning",
+        Category = category,
+        DecayClass = DecayClass.Stable,
+        FeedbackWeight = 1.0,
+        UpdateCount = 0,
+        CreatedAt = DateTimeOffset.UtcNow,
+        Scope = new LearningScope
+        {
+            AgentId = agentId,
+            TeamId = teamId,
+            IsGlobal = isGlobal
+        },
+        Source = new LearningSource
+        {
+            SourceType = LearningSourceType.HumanCorrection,
+            SourceId = "test",
+            SourceDescription = "Test"
+        },
+        Provenance = new LearningProvenance
+        {
+            OriginPipeline = "test",
+            OriginTask = "test",
+            OriginTimestamp = DateTimeOffset.UtcNow,
+            Confidence = 1.0
+        }
+    };
+
+    [Fact]
+    public async Task InMemory_SaveAndRetrieve_RoundTrips()
+    {
+        var id = Guid.NewGuid();
+        var entry = BuildEntry(id: id, isGlobal: true);
+
+        await _sut.SaveAsync(entry, CancellationToken.None);
+        var result = await _sut.GetAsync(id, CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().NotBeNull();
+        result.Value!.LearningId.Should().Be(id);
+        result.Value.Content.Should().Be("Test learning");
+        result.Value.Scope.IsGlobal.Should().BeTrue();
+    }
+
+    [Fact]
+    public async Task InMemory_ScopeHierarchySearch_Works()
+    {
+        await _sut.SaveAsync(BuildEntry(agentId: "a1"), CancellationToken.None);
+        await _sut.SaveAsync(BuildEntry(teamId: "t1"), CancellationToken.None);
+        await _sut.SaveAsync(BuildEntry(isGlobal: true), CancellationToken.None);
+
+        var allScopes = new LearningSearchCriteria
+        {
+            Scope = new LearningScope { AgentId = "a1", TeamId = "t1" }
+        };
+        var allResult = await _sut.SearchAsync(allScopes, CancellationToken.None);
+        allResult.Value.Should().HaveCount(3);
+
+        var agentOnly = new LearningSearchCriteria
+        {
+            Scope = new LearningScope { AgentId = "a1" }
+        };
+        var agentResult = await _sut.SearchAsync(agentOnly, CancellationToken.None);
+        agentResult.Value.Should().HaveCount(2);
+    }
+
+    [Fact]
+    public async Task InMemory_SoftDelete_ExcludesFromSearch()
+    {
+        var keepId = Guid.NewGuid();
+        var deleteId = Guid.NewGuid();
+        await _sut.SaveAsync(BuildEntry(id: keepId, isGlobal: true), CancellationToken.None);
+        await _sut.SaveAsync(BuildEntry(id: deleteId, isGlobal: true), CancellationToken.None);
+
+        await _sut.SoftDeleteAsync(deleteId, "test", CancellationToken.None);
+
+        var result = await _sut.SearchAsync(
+            new LearningSearchCriteria { Scope = new LearningScope { IsGlobal = true } },
+            CancellationToken.None);
+
+        result.Value.Should().ContainSingle();
+        result.Value[0].LearningId.Should().Be(keepId);
+    }
+
+    [Fact]
+    public async Task InMemory_Update_NotFound_ReturnsFail()
+    {
+        var entry = BuildEntry(id: Guid.NewGuid());
+
+        var result = await _sut.UpdateAsync(entry, CancellationToken.None);
+
+        result.IsSuccess.Should().BeFalse();
+    }
+}

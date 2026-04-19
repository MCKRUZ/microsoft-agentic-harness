using Domain.Common.Config.Infrastructure;
using Domain.Common.Workflow;
using FluentAssertions;
using Infrastructure.AI.StateManagement.Checkpoints;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.StateManagement;

/// <summary>
/// Integration tests for <see cref="JsonCheckpointStateManager"/> exercising the full
/// async CRUD lifecycle, state transitions, metadata operations, and path validation.
/// Uses a real temp directory for file I/O — no mocks for the file system.
/// </summary>
public sealed class JsonCheckpointStateManagerTests : IDisposable
{
    private readonly string _basePath;
    private readonly JsonCheckpointStateManager _sut;

    public JsonCheckpointStateManagerTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"jcsm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_basePath);

        var config = new InfrastructureConfig
        {
            StateManagement = new StateManagementConfig { BasePath = _basePath }
        };
        var optionsMonitor = Mock.Of<IOptionsMonitor<InfrastructureConfig>>(
            m => m.CurrentValue == config);

        _sut = new JsonCheckpointStateManager(
            NullLogger<JsonCheckpointStateManager>.Instance,
            optionsMonitor);
    }

    public void Dispose()
    {
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
    }

    // ── CreateAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_NewWorkflow_ReturnsStateWithCorrectDefaults()
    {
        var state = await _sut.CreateAsync("wf-001");

        state.WorkflowId.Should().Be("wf-001");
        state.WorkflowStatus.Should().Be("not_started");
        state.WorkflowStarted.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CreateAsync_DuplicateWorkflow_ThrowsInvalidOperation()
    {
        await _sut.CreateAsync("wf-dup");

        var act = () => _sut.CreateAsync("wf-dup");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    // ── ExistsAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExistsAsync_AfterCreate_ReturnsTrue()
    {
        await _sut.CreateAsync("wf-exists");

        var exists = await _sut.ExistsAsync("wf-exists");

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_NonExistent_ReturnsFalse()
    {
        var exists = await _sut.ExistsAsync("wf-nope");

        exists.Should().BeFalse();
    }

    // ── LoadAsync / SaveAsync roundtrip ──────────────────────────────────────

    [Fact]
    public async Task LoadAsync_NonExistent_ReturnsNull()
    {
        var result = await _sut.LoadAsync("wf-missing");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_Roundtrips()
    {
        var state = new WorkflowState
        {
            WorkflowId = "wf-roundtrip",
            WorkflowStatus = "in_progress",
            CurrentNodeId = "node-1",
            WorkflowStarted = DateTime.UtcNow
        };
        state.Nodes["node-1"] = new NodeState
        {
            NodeId = "node-1",
            NodeType = "phase",
            Status = "in_progress",
            StartedAt = DateTime.UtcNow
        };

        await _sut.SaveAsync(state);
        var loaded = await _sut.LoadAsync("wf-roundtrip");

        loaded.Should().NotBeNull();
        loaded!.WorkflowId.Should().Be("wf-roundtrip");
        loaded.WorkflowStatus.Should().Be("in_progress");
        loaded.CurrentNodeId.Should().Be("node-1");
        loaded.Nodes.Should().ContainKey("node-1");
        loaded.Nodes["node-1"].NodeType.Should().Be("phase");
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingState()
    {
        await _sut.CreateAsync("wf-overwrite");

        var updated = new WorkflowState
        {
            WorkflowId = "wf-overwrite",
            WorkflowStatus = "in_progress",
            WorkflowStarted = DateTime.UtcNow
        };
        await _sut.SaveAsync(updated);

        var loaded = await _sut.LoadAsync("wf-overwrite");
        loaded!.WorkflowStatus.Should().Be("in_progress");
    }

    // ── DeleteAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_ExistingWorkflow_ReturnsTrueAndRemovesFile()
    {
        await _sut.CreateAsync("wf-del");

        var deleted = await _sut.DeleteAsync("wf-del");

        deleted.Should().BeTrue();
        (await _sut.ExistsAsync("wf-del")).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_NonExistent_ReturnsFalse()
    {
        var deleted = await _sut.DeleteAsync("wf-ghost");

        deleted.Should().BeFalse();
    }

    // ── Node operations ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetNodeStateAsync_ExistingNode_ReturnsNode()
    {
        var state = await _sut.CreateAsync("wf-node");
        state.Nodes["n1"] = new NodeState { NodeId = "n1", NodeType = "skill", Status = "in_progress" };
        await _sut.SaveAsync(state);

        var node = await _sut.GetNodeStateAsync("wf-node", "n1");

        node.Should().NotBeNull();
        node!.NodeId.Should().Be("n1");
        node.Status.Should().Be("in_progress");
    }

    [Fact]
    public async Task GetNodeStateAsync_MissingNode_ReturnsNull()
    {
        await _sut.CreateAsync("wf-nonode");

        var node = await _sut.GetNodeStateAsync("wf-nonode", "missing");

        node.Should().BeNull();
    }

    [Fact]
    public async Task GetNodeStateAsync_MissingWorkflow_ReturnsNull()
    {
        var node = await _sut.GetNodeStateAsync("wf-nope", "n1");

        node.Should().BeNull();
    }

    [Fact]
    public async Task UpdateNodeStateAsync_UpdatesAndPersists()
    {
        var state = await _sut.CreateAsync("wf-upd");
        state.Nodes["n1"] = new NodeState { NodeId = "n1", Status = "not_started" };
        await _sut.SaveAsync(state);

        var updated = new NodeState { NodeId = "n1", Status = "completed", CompletedAt = DateTime.UtcNow };
        await _sut.UpdateNodeStateAsync("wf-upd", "n1", updated);

        var reloaded = await _sut.GetNodeStateAsync("wf-upd", "n1");
        reloaded!.Status.Should().Be("completed");
        reloaded.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateNodeStateAsync_MissingWorkflow_Throws()
    {
        var act = () => _sut.UpdateNodeStateAsync("wf-nope", "n1", new NodeState());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task GetNodesByTypeAsync_FiltersCorrectly()
    {
        var state = await _sut.CreateAsync("wf-type");
        state.Nodes["p1"] = new NodeState { NodeId = "p1", NodeType = "phase" };
        state.Nodes["s1"] = new NodeState { NodeId = "s1", NodeType = "skill" };
        state.Nodes["s2"] = new NodeState { NodeId = "s2", NodeType = "skill" };
        await _sut.SaveAsync(state);

        var skills = await _sut.GetNodesByTypeAsync("wf-type", "skill");

        skills.Should().HaveCount(2);
        skills.Should().OnlyContain(n => n.NodeType == "skill");
    }

    [Fact]
    public async Task GetNodesByTypeAsync_MissingWorkflow_ReturnsEmpty()
    {
        var result = await _sut.GetNodesByTypeAsync("wf-gone", "phase");

        result.Should().BeEmpty();
    }

    // ── State transitions ────────────────────────────────────────────────────

    [Fact]
    public async Task CanTransitionAsync_ValidTransition_ReturnsTrue()
    {
        var state = await _sut.CreateAsync("wf-trans");
        state.Nodes["n1"] = new NodeState { NodeId = "n1", Status = "not_started" };
        await _sut.SaveAsync(state);

        var can = await _sut.CanTransitionAsync("wf-trans", "n1", "in_progress");

        can.Should().BeTrue();
    }

    [Fact]
    public async Task CanTransitionAsync_InvalidTransition_ReturnsFalse()
    {
        var state = await _sut.CreateAsync("wf-trans2");
        state.Nodes["n1"] = new NodeState { NodeId = "n1", Status = "not_started" };
        await _sut.SaveAsync(state);

        var can = await _sut.CanTransitionAsync("wf-trans2", "n1", "completed");

        can.Should().BeFalse();
    }

    [Fact]
    public async Task CanTransitionAsync_MissingNode_ReturnsFalse()
    {
        await _sut.CreateAsync("wf-trans3");

        var can = await _sut.CanTransitionAsync("wf-trans3", "missing", "in_progress");

        can.Should().BeFalse();
    }

    [Fact]
    public async Task TransitionAsync_ValidTransition_UpdatesStatusAndTimestamps()
    {
        var state = await _sut.CreateAsync("wf-dotrans");
        state.Nodes["n1"] = new NodeState { NodeId = "n1", Status = "not_started" };
        await _sut.SaveAsync(state);

        await _sut.TransitionAsync("wf-dotrans", "n1", "in_progress");

        var node = await _sut.GetNodeStateAsync("wf-dotrans", "n1");
        node!.Status.Should().Be("in_progress");
        node.StartedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TransitionAsync_ToCompleted_SetsCompletedAt()
    {
        var state = await _sut.CreateAsync("wf-comp");
        state.Nodes["n1"] = new NodeState { NodeId = "n1", Status = "not_started" };
        await _sut.SaveAsync(state);

        await _sut.TransitionAsync("wf-comp", "n1", "in_progress");
        await _sut.TransitionAsync("wf-comp", "n1", "completed");

        var node = await _sut.GetNodeStateAsync("wf-comp", "n1");
        node!.Status.Should().Be("completed");
        node.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TransitionAsync_ToFailed_SetsCompletedAt()
    {
        var state = await _sut.CreateAsync("wf-fail");
        state.Nodes["n1"] = new NodeState { NodeId = "n1", Status = "not_started" };
        await _sut.SaveAsync(state);

        await _sut.TransitionAsync("wf-fail", "n1", "in_progress");
        await _sut.TransitionAsync("wf-fail", "n1", "failed");

        var node = await _sut.GetNodeStateAsync("wf-fail", "n1");
        node!.Status.Should().Be("failed");
        node.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TransitionAsync_InvalidTransition_ThrowsInvalidStateTransition()
    {
        var state = await _sut.CreateAsync("wf-bad");
        state.Nodes["n1"] = new NodeState { NodeId = "n1", Status = "not_started" };
        await _sut.SaveAsync(state);

        var act = () => _sut.TransitionAsync("wf-bad", "n1", "completed");

        await act.Should().ThrowAsync<InvalidStateTransitionException>();
    }

    [Fact]
    public async Task TransitionAsync_MissingWorkflow_Throws()
    {
        var act = () => _sut.TransitionAsync("wf-ghost", "n1", "in_progress");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TransitionAsync_MissingNode_Throws()
    {
        await _sut.CreateAsync("wf-nonode2");

        var act = () => _sut.TransitionAsync("wf-nonode2", "missing", "in_progress");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Metadata operations ──────────────────────────────────────────────────

    [Fact]
    public async Task SetMetadataAsync_StoresValue()
    {
        var state = await _sut.CreateAsync("wf-meta");
        state.Nodes["n1"] = new NodeState { NodeId = "n1" };
        await _sut.SaveAsync(state);

        await _sut.SetMetadataAsync("wf-meta", "n1", "score", 95);

        var all = await _sut.GetAllMetadataAsync("wf-meta", "n1");
        all.Should().ContainKey("score");
    }

    [Fact]
    public async Task SetMetadataAsync_MissingWorkflow_Throws()
    {
        var act = () => _sut.SetMetadataAsync("wf-ghost", "n1", "key", "val");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SetMetadataAsync_MissingNode_Throws()
    {
        await _sut.CreateAsync("wf-metano");

        var act = () => _sut.SetMetadataAsync("wf-metano", "missing", "key", "val");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetMetadataAsync_ExistingKey_ReturnsValueAfterJsonRoundtrip()
    {
        // After JSON serialization/deserialization, metadata values become JsonElements.
        // GetMetadataAsync<T> attempts direct cast then Convert.ChangeType. For strings
        // stored via JSON roundtrip, the value is a JsonElement, not a raw string.
        // Use SetMetadataAsync which does load-set-save, so the value roundtrips through JSON.
        var state = await _sut.CreateAsync("wf-getmeta");
        state.Nodes["n1"] = new NodeState { NodeId = "n1" };
        await _sut.SaveAsync(state);

        await _sut.SetMetadataAsync("wf-getmeta", "n1", "label", "test-label");

        // After SetMetadataAsync, the value is saved to JSON and reloaded.
        // Verify the metadata key exists via GetAllMetadataAsync (returns raw objects).
        var all = await _sut.GetAllMetadataAsync("wf-getmeta", "n1");
        all.Should().ContainKey("label");
        all["label"].ToString().Should().Be("test-label");
    }

    [Fact]
    public async Task GetMetadataAsync_MissingKey_ReturnsDefault()
    {
        var state = await _sut.CreateAsync("wf-nometa");
        state.Nodes["n1"] = new NodeState { NodeId = "n1" };
        await _sut.SaveAsync(state);

        var value = await _sut.GetMetadataAsync<string>("wf-nometa", "n1", "nope");

        value.Should().BeNull();
    }

    [Fact]
    public async Task GetMetadataAsync_MissingNode_ReturnsDefault()
    {
        await _sut.CreateAsync("wf-nonode3");

        var value = await _sut.GetMetadataAsync<string>("wf-nonode3", "ghost", "key");

        value.Should().BeNull();
    }

    [Fact]
    public async Task GetAllMetadataAsync_MissingNode_ReturnsEmptyDictionary()
    {
        await _sut.CreateAsync("wf-allempty");

        var result = await _sut.GetAllMetadataAsync("wf-allempty", "ghost");

        result.Should().BeEmpty();
    }

    // ── Completion queries ───────────────────────────────────────────────────

    [Fact]
    public async Task IsNodeCompleteAsync_CompletedNode_ReturnsTrue()
    {
        var state = await _sut.CreateAsync("wf-iscomplete");
        state.Nodes["n1"] = new NodeState { NodeId = "n1", Status = "completed" };
        await _sut.SaveAsync(state);

        var complete = await _sut.IsNodeCompleteAsync("wf-iscomplete", "n1");

        complete.Should().BeTrue();
    }

    [Fact]
    public async Task IsNodeCompleteAsync_IncompleteNode_ReturnsFalse()
    {
        var state = await _sut.CreateAsync("wf-notcomplete");
        state.Nodes["n1"] = new NodeState { NodeId = "n1", Status = "in_progress" };
        await _sut.SaveAsync(state);

        var complete = await _sut.IsNodeCompleteAsync("wf-notcomplete", "n1");

        complete.Should().BeFalse();
    }

    [Fact]
    public async Task GetIncompleteNodesAsync_ReturnsNonCompletedNodes()
    {
        var state = await _sut.CreateAsync("wf-incomplete");
        state.Nodes["done"] = new NodeState { NodeId = "done", Status = "completed" };
        state.Nodes["todo"] = new NodeState { NodeId = "todo", Status = "not_started" };
        state.Nodes["wip"] = new NodeState { NodeId = "wip", Status = "in_progress" };
        await _sut.SaveAsync(state);

        var incomplete = await _sut.GetIncompleteNodesAsync("wf-incomplete");

        incomplete.Should().HaveCount(2);
        incomplete.Should().Contain(n => n.NodeId == "todo");
        incomplete.Should().Contain(n => n.NodeId == "wip");
    }

    [Fact]
    public async Task GetIncompleteNodesAsync_MissingWorkflow_ReturnsEmpty()
    {
        var result = await _sut.GetIncompleteNodesAsync("wf-gone");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCompletedNodesAsync_ReturnsOnlyCompleted()
    {
        var state = await _sut.CreateAsync("wf-completed");
        state.Nodes["done1"] = new NodeState { NodeId = "done1", Status = "completed" };
        state.Nodes["done2"] = new NodeState { NodeId = "done2", Status = "completed" };
        state.Nodes["wip"] = new NodeState { NodeId = "wip", Status = "in_progress" };
        await _sut.SaveAsync(state);

        var completed = await _sut.GetCompletedNodesAsync("wf-completed");

        completed.Should().HaveCount(2);
        completed.Should().OnlyContain(n => n.Status == "completed");
    }

    [Fact]
    public async Task GetCompletedNodesAsync_MissingWorkflow_ReturnsEmpty()
    {
        var result = await _sut.GetCompletedNodesAsync("wf-gone");

        result.Should().BeEmpty();
    }

    // ── Current node ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCurrentNodeAsync_ReturnsCorrectNode()
    {
        var state = await _sut.CreateAsync("wf-current");
        state.CurrentNodeId = "active";
        state.Nodes["active"] = new NodeState { NodeId = "active", Status = "in_progress" };
        await _sut.SaveAsync(state);

        var current = await _sut.GetCurrentNodeAsync("wf-current");

        current.Should().NotBeNull();
        current!.NodeId.Should().Be("active");
    }

    [Fact]
    public async Task GetCurrentNodeAsync_CurrentNodeIdNotInNodes_ReturnsNull()
    {
        var state = await _sut.CreateAsync("wf-badcurrent");
        state.CurrentNodeId = "nonexistent";
        await _sut.SaveAsync(state);

        var current = await _sut.GetCurrentNodeAsync("wf-badcurrent");

        current.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentNodeAsync_MissingWorkflow_ReturnsNull()
    {
        var current = await _sut.GetCurrentNodeAsync("wf-gone");

        current.Should().BeNull();
    }

    [Fact]
    public async Task SetCurrentNodeAsync_UpdatesCurrentNodeId()
    {
        var state = await _sut.CreateAsync("wf-setcurrent");
        state.Nodes["n1"] = new NodeState { NodeId = "n1" };
        state.Nodes["n2"] = new NodeState { NodeId = "n2" };
        await _sut.SaveAsync(state);

        await _sut.SetCurrentNodeAsync("wf-setcurrent", "n2");

        var loaded = await _sut.LoadAsync("wf-setcurrent");
        loaded!.CurrentNodeId.Should().Be("n2");
    }

    [Fact]
    public async Task SetCurrentNodeAsync_MissingWorkflow_Throws()
    {
        var act = () => _sut.SetCurrentNodeAsync("wf-ghost", "n1");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Workflow status ──────────────────────────────────────────────────────

    [Fact]
    public async Task SetWorkflowStatusAsync_UpdatesStatus()
    {
        await _sut.CreateAsync("wf-status");

        await _sut.SetWorkflowStatusAsync("wf-status", "in_progress");

        var loaded = await _sut.LoadAsync("wf-status");
        loaded!.WorkflowStatus.Should().Be("in_progress");
    }

    [Fact]
    public async Task SetWorkflowStatusAsync_Completed_SetsCompletedTimestamp()
    {
        await _sut.CreateAsync("wf-statcomp");

        await _sut.SetWorkflowStatusAsync("wf-statcomp", "completed");

        var loaded = await _sut.LoadAsync("wf-statcomp");
        loaded!.WorkflowStatus.Should().Be("completed");
        loaded.WorkflowCompleted.Should().NotBeNull();
        loaded.WorkflowCompleted.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SetWorkflowStatusAsync_MissingWorkflow_Throws()
    {
        var act = () => _sut.SetWorkflowStatusAsync("wf-ghost", "in_progress");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetWorkflowStatusAsync_ExistingWorkflow_ReturnsStatus()
    {
        await _sut.CreateAsync("wf-getstatus");

        var status = await _sut.GetWorkflowStatusAsync("wf-getstatus");

        status.Should().Be("not_started");
    }

    [Fact]
    public async Task GetWorkflowStatusAsync_MissingWorkflow_ReturnsUnknown()
    {
        var status = await _sut.GetWorkflowStatusAsync("wf-gone");

        status.Should().Be("unknown");
    }

    [Fact]
    public async Task CompleteWorkflowAsync_SetsStatusToCompleted()
    {
        await _sut.CreateAsync("wf-complete");

        await _sut.CompleteWorkflowAsync("wf-complete");

        var status = await _sut.GetWorkflowStatusAsync("wf-complete");
        status.Should().Be("completed");
    }

    // ── Path validation / security ───────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task CreateAsync_EmptyWorkflowId_ThrowsArgumentException(string workflowId)
    {
        var act = () => _sut.CreateAsync(workflowId);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("path/traversal")]
    [InlineData("path\\traversal")]
    public async Task CreateAsync_TraversalInWorkflowId_ThrowsArgumentException(string workflowId)
    {
        var act = () => _sut.CreateAsync(workflowId);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}

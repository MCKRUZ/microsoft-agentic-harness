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
/// CRUD lifecycle, node state operations, transitions, metadata, and path validation.
/// Uses a temp directory per test to avoid cross-test interference.
/// </summary>
public sealed class JsonCheckpointStateManagerIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonCheckpointStateManager _sut;

    public JsonCheckpointStateManagerIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"json-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new InfrastructureConfig
        {
            StateManagement = new StateManagementConfig { BasePath = _tempDir }
        };
        var options = Mock.Of<IOptionsMonitor<InfrastructureConfig>>(
            o => o.CurrentValue == config);

        _sut = new JsonCheckpointStateManager(
            NullLogger<JsonCheckpointStateManager>.Instance, options);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task CreateAsync_NewWorkflow_PersistsToJsonFile()
    {
        var state = await _sut.CreateAsync("wf-001");

        state.WorkflowId.Should().Be("wf-001");
        state.WorkflowStatus.Should().Be("not_started");

        var exists = await _sut.ExistsAsync("wf-001");
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_DuplicateWorkflow_Throws()
    {
        await _sut.CreateAsync("wf-dup");

        var act = () => _sut.CreateAsync("wf-dup");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public async Task LoadAsync_NonExistent_ReturnsNull()
    {
        var result = await _sut.LoadAsync("does-not-exist");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_RoundTrips_WorkflowState()
    {
        var original = new WorkflowState
        {
            WorkflowId = "wf-round",
            WorkflowStatus = "in_progress",
            WorkflowStarted = DateTime.UtcNow,
            CurrentNodeId = "node-1"
        };

        await _sut.SaveAsync(original);
        var loaded = await _sut.LoadAsync("wf-round");

        loaded.Should().NotBeNull();
        loaded!.WorkflowId.Should().Be("wf-round");
        loaded.WorkflowStatus.Should().Be("in_progress");
        loaded.CurrentNodeId.Should().Be("node-1");
    }

    [Fact]
    public async Task DeleteAsync_ExistingWorkflow_RemovesFile()
    {
        await _sut.CreateAsync("wf-del");
        (await _sut.ExistsAsync("wf-del")).Should().BeTrue();

        var deleted = await _sut.DeleteAsync("wf-del");

        deleted.Should().BeTrue();
        (await _sut.ExistsAsync("wf-del")).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_NonExistent_ReturnsFalse()
    {
        var deleted = await _sut.DeleteAsync("nope");

        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateNodeStateAsync_CreatesAndLoadsNode()
    {
        await _sut.CreateAsync("wf-node");
        var node = new NodeState
        {
            NodeId = "phase-0",
            NodeType = "phase",
            Status = "not_started"
        };

        await _sut.UpdateNodeStateAsync("wf-node", "phase-0", node);
        var loaded = await _sut.GetNodeStateAsync("wf-node", "phase-0");

        loaded.Should().NotBeNull();
        loaded!.NodeId.Should().Be("phase-0");
        loaded.Status.Should().Be("not_started");
    }

    [Fact]
    public async Task GetNodesByTypeAsync_FiltersCorrectly()
    {
        await _sut.CreateAsync("wf-types");
        await _sut.UpdateNodeStateAsync("wf-types", "p0",
            new NodeState { NodeId = "p0", NodeType = "phase", Status = "not_started" });
        await _sut.UpdateNodeStateAsync("wf-types", "t0",
            new NodeState { NodeId = "t0", NodeType = "task", Status = "not_started" });
        await _sut.UpdateNodeStateAsync("wf-types", "p1",
            new NodeState { NodeId = "p1", NodeType = "phase", Status = "not_started" });

        var phases = await _sut.GetNodesByTypeAsync("wf-types", "phase");

        phases.Should().HaveCount(2);
        phases.Should().AllSatisfy(n => n.NodeType.Should().Be("phase"));
    }

    [Fact]
    public async Task TransitionAsync_ValidTransition_UpdatesStatus()
    {
        await _sut.CreateAsync("wf-trans");
        await _sut.UpdateNodeStateAsync("wf-trans", "n1",
            new NodeState { NodeId = "n1", NodeType = "phase", Status = "not_started" });

        await _sut.TransitionAsync("wf-trans", "n1", "in_progress");

        var node = await _sut.GetNodeStateAsync("wf-trans", "n1");
        node!.Status.Should().Be("in_progress");
        node.StartedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TransitionAsync_InvalidTransition_Throws()
    {
        await _sut.CreateAsync("wf-bad-trans");
        await _sut.UpdateNodeStateAsync("wf-bad-trans", "n1",
            new NodeState { NodeId = "n1", NodeType = "phase", Status = "not_started" });

        var act = () => _sut.TransitionAsync("wf-bad-trans", "n1", "completed");

        await act.Should().ThrowAsync<InvalidStateTransitionException>();
    }

    [Fact]
    public async Task TransitionAsync_ToCompleted_SetsCompletedAt()
    {
        await _sut.CreateAsync("wf-complete");
        await _sut.UpdateNodeStateAsync("wf-complete", "n1",
            new NodeState { NodeId = "n1", NodeType = "phase", Status = "not_started" });

        await _sut.TransitionAsync("wf-complete", "n1", "in_progress");
        await _sut.TransitionAsync("wf-complete", "n1", "completed");

        var node = await _sut.GetNodeStateAsync("wf-complete", "n1");
        node!.Status.Should().Be("completed");
        node.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SetMetadataAsync_StoresAndRetrieves()
    {
        await _sut.CreateAsync("wf-meta");
        await _sut.UpdateNodeStateAsync("wf-meta", "n1",
            new NodeState { NodeId = "n1", NodeType = "phase", Status = "not_started" });

        await _sut.SetMetadataAsync("wf-meta", "n1", "score", 42);

        var all = await _sut.GetAllMetadataAsync("wf-meta", "n1");
        all.Should().ContainKey("score");
    }

    [Fact]
    public async Task GetMetadataAsync_MissingKey_ReturnsDefault()
    {
        await _sut.CreateAsync("wf-meta2");
        await _sut.UpdateNodeStateAsync("wf-meta2", "n1",
            new NodeState { NodeId = "n1", NodeType = "phase", Status = "not_started" });

        var value = await _sut.GetMetadataAsync<string>("wf-meta2", "n1", "missing");

        value.Should().BeNull();
    }

    [Fact]
    public async Task GetMetadataAsync_MissingNode_ReturnsDefault()
    {
        await _sut.CreateAsync("wf-meta3");

        var value = await _sut.GetMetadataAsync<string>("wf-meta3", "nonexistent", "key");

        value.Should().BeNull();
    }

    [Fact]
    public async Task IsNodeCompleteAsync_Completed_ReturnsTrue()
    {
        await _sut.CreateAsync("wf-complete2");
        await _sut.UpdateNodeStateAsync("wf-complete2", "n1",
            new NodeState { NodeId = "n1", NodeType = "phase", Status = "completed" });

        var complete = await _sut.IsNodeCompleteAsync("wf-complete2", "n1");

        complete.Should().BeTrue();
    }

    [Fact]
    public async Task GetIncompleteNodesAsync_ReturnsOnlyIncomplete()
    {
        await _sut.CreateAsync("wf-incomplete");
        await _sut.UpdateNodeStateAsync("wf-incomplete", "done",
            new NodeState { NodeId = "done", NodeType = "phase", Status = "completed" });
        await _sut.UpdateNodeStateAsync("wf-incomplete", "pending",
            new NodeState { NodeId = "pending", NodeType = "phase", Status = "in_progress" });

        var incomplete = await _sut.GetIncompleteNodesAsync("wf-incomplete");

        incomplete.Should().ContainSingle(n => n.NodeId == "pending");
    }

    [Fact]
    public async Task SetCurrentNodeAsync_Updates_CurrentNode()
    {
        await _sut.CreateAsync("wf-current");
        await _sut.UpdateNodeStateAsync("wf-current", "n1",
            new NodeState { NodeId = "n1", NodeType = "phase", Status = "not_started" });

        await _sut.SetCurrentNodeAsync("wf-current", "n1");

        var current = await _sut.GetCurrentNodeAsync("wf-current");
        current.Should().NotBeNull();
        current!.NodeId.Should().Be("n1");
    }

    [Fact]
    public async Task SetWorkflowStatusAsync_Completed_SetsTimestamp()
    {
        await _sut.CreateAsync("wf-status");

        await _sut.SetWorkflowStatusAsync("wf-status", "completed");

        var status = await _sut.GetWorkflowStatusAsync("wf-status");
        status.Should().Be("completed");
    }

    [Fact]
    public async Task CompleteWorkflowAsync_SetsCompletedStatus()
    {
        await _sut.CreateAsync("wf-finish");

        await _sut.CompleteWorkflowAsync("wf-finish");

        var status = await _sut.GetWorkflowStatusAsync("wf-finish");
        status.Should().Be("completed");
    }

    [Fact]
    public async Task GetWorkflowStatusAsync_NonExistent_ReturnsUnknown()
    {
        var status = await _sut.GetWorkflowStatusAsync("nonexistent");

        status.Should().Be("unknown");
    }

    [Fact]
    public async Task CanTransitionAsync_NonExistentNode_ReturnsFalse()
    {
        await _sut.CreateAsync("wf-can");

        var canTransition = await _sut.CanTransitionAsync("wf-can", "missing", "in_progress");

        canTransition.Should().BeFalse();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("wf/bad")]
    [InlineData("wf\\bad")]
    public void GetStateFilePath_TraversalAttempt_Throws(string badId)
    {
        var act = () => _sut.LoadAsync(badId);

        act.Should().ThrowAsync<ArgumentException>();
    }
}

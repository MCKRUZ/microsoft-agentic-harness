using Domain.Common.Config.Infrastructure;
using Domain.Common.Workflow;
using FluentAssertions;
using Infrastructure.AI.Generators;
using Infrastructure.AI.StateManagement;
using Infrastructure.AI.StateManagement.Checkpoints;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.StateManagement;

/// <summary>
/// Integration tests for <see cref="MarkdownCheckpointDecorator"/> verifying that
/// markdown files are generated alongside JSON persistence, and that delegation
/// to the inner state manager works correctly.
/// </summary>
public sealed class MarkdownCheckpointDecoratorTests : IDisposable
{
    private readonly string _basePath;
    private readonly IOptionsMonitor<InfrastructureConfig> _optionsMonitor;
    private readonly JsonCheckpointStateManager _inner;
    private readonly MarkdownCheckpointDecorator _sut;

    public MarkdownCheckpointDecoratorTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"md-dec-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_basePath);

        var config = new InfrastructureConfig
        {
            StateManagement = new StateManagementConfig
            {
                BasePath = _basePath,
                EnableMarkdownGeneration = true
            }
        };
        _optionsMonitor = Mock.Of<IOptionsMonitor<InfrastructureConfig>>(
            m => m.CurrentValue == config);

        _inner = new JsonCheckpointStateManager(
            NullLogger<JsonCheckpointStateManager>.Instance,
            _optionsMonitor);

        _sut = new MarkdownCheckpointDecorator(
            _inner,
            NullLogger<MarkdownCheckpointDecorator>.Instance,
            new StateMarkdownGenerator(),
            _optionsMonitor);
    }

    public void Dispose()
    {
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
    }

    private string GetJsonPath(string workflowId) =>
        Path.Combine(_basePath, workflowId, "checkpoints", "workflow-state.json");

    private string GetMarkdownPath(string workflowId) =>
        Path.Combine(_basePath, workflowId, "inputs", "workflow-state.md");

    // ── SaveAsync: markdown generation ───────────────────────────────────────

    [Fact]
    public async Task SaveAsync_GeneratesMarkdownFile()
    {
        var state = new WorkflowState
        {
            WorkflowId = "wf-md-save",
            WorkflowStatus = "in_progress",
            CurrentNodeId = "phase-1",
            WorkflowStarted = DateTime.UtcNow
        };

        await _sut.SaveAsync(state);

        File.Exists(GetMarkdownPath("wf-md-save")).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_MarkdownContainsWorkflowInfo()
    {
        var state = new WorkflowState
        {
            WorkflowId = "wf-md-content",
            WorkflowStatus = "in_progress",
            CurrentNodeId = "discovery",
            WorkflowStarted = DateTime.UtcNow
        };
        state.Nodes["discovery"] = new NodeState
        {
            NodeId = "discovery",
            NodeType = "phase",
            Status = "in_progress",
            StartedAt = DateTime.UtcNow
        };

        await _sut.SaveAsync(state);

        var md = await File.ReadAllTextAsync(GetMarkdownPath("wf-md-content"));
        md.Should().Contain("workflow_id: wf-md-content");
        md.Should().Contain("workflow_status: in_progress");
        md.Should().Contain("current_node_id: discovery");
        md.Should().Contain("### discovery (phase)");
    }

    [Fact]
    public async Task SaveAsync_AlsoSavesJsonViaInner()
    {
        var state = new WorkflowState
        {
            WorkflowId = "wf-md-json",
            WorkflowStatus = "not_started",
            WorkflowStarted = DateTime.UtcNow
        };

        await _sut.SaveAsync(state);

        File.Exists(GetJsonPath("wf-md-json")).Should().BeTrue();
        var loaded = await _inner.LoadAsync("wf-md-json");
        loaded.Should().NotBeNull();
        loaded!.WorkflowId.Should().Be("wf-md-json");
    }

    [Fact]
    public async Task SaveAsync_UpdateOverwritesMarkdown()
    {
        var state = new WorkflowState
        {
            WorkflowId = "wf-md-overwrite",
            WorkflowStatus = "not_started",
            WorkflowStarted = DateTime.UtcNow
        };
        await _sut.SaveAsync(state);

        state.WorkflowStatus = "completed";
        state.WorkflowCompleted = DateTime.UtcNow;
        await _sut.SaveAsync(state);

        var md = await File.ReadAllTextAsync(GetMarkdownPath("wf-md-overwrite"));
        md.Should().Contain("workflow_status: completed");
        md.Should().Contain("workflow_completed:");
    }

    // ── SaveAsync: markdown disabled ─────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_MarkdownDisabled_NoMarkdownFile()
    {
        var config = new InfrastructureConfig
        {
            StateManagement = new StateManagementConfig
            {
                BasePath = _basePath,
                EnableMarkdownGeneration = false
            }
        };
        var opts = Mock.Of<IOptionsMonitor<InfrastructureConfig>>(m => m.CurrentValue == config);

        var inner = new JsonCheckpointStateManager(
            NullLogger<JsonCheckpointStateManager>.Instance, opts);
        var decorator = new MarkdownCheckpointDecorator(
            inner,
            NullLogger<MarkdownCheckpointDecorator>.Instance,
            new StateMarkdownGenerator(),
            opts);

        var state = new WorkflowState
        {
            WorkflowId = "wf-no-md",
            WorkflowStatus = "not_started",
            WorkflowStarted = DateTime.UtcNow
        };
        await decorator.SaveAsync(state);

        // JSON created by inner
        File.Exists(GetJsonPath("wf-no-md")).Should().BeTrue();
        // No markdown
        File.Exists(GetMarkdownPath("wf-no-md")).Should().BeFalse();
    }

    // ── LoadAsync: delegates to inner ────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_DelegatesToInner()
    {
        var state = new WorkflowState
        {
            WorkflowId = "wf-md-load",
            WorkflowStatus = "in_progress",
            WorkflowStarted = DateTime.UtcNow
        };
        await _sut.SaveAsync(state);

        var loaded = await _sut.LoadAsync("wf-md-load");

        loaded.Should().NotBeNull();
        loaded!.WorkflowId.Should().Be("wf-md-load");
    }

    [Fact]
    public async Task LoadAsync_NonExistent_ReturnsNull()
    {
        var loaded = await _sut.LoadAsync("wf-ghost");

        loaded.Should().BeNull();
    }

    // ── ExistsAsync: delegates to inner ──────────────────────────────────────

    [Fact]
    public async Task ExistsAsync_DelegatesToInner()
    {
        var state = new WorkflowState
        {
            WorkflowId = "wf-md-exists",
            WorkflowStatus = "not_started",
            WorkflowStarted = DateTime.UtcNow
        };
        await _sut.SaveAsync(state);

        (await _sut.ExistsAsync("wf-md-exists")).Should().BeTrue();
        (await _sut.ExistsAsync("wf-nope")).Should().BeFalse();
    }

    // ── CreateAsync: delegates to inner ──────────────────────────────────────

    [Fact]
    public async Task CreateAsync_DelegatesToInnerAndReturnsState()
    {
        var state = await _sut.CreateAsync("wf-md-create");

        state.WorkflowId.Should().Be("wf-md-create");
        state.WorkflowStatus.Should().Be("not_started");
        File.Exists(GetJsonPath("wf-md-create")).Should().BeTrue();
    }

    // ── DeleteAsync: removes markdown too ────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_RemovesBothJsonAndMarkdown()
    {
        var state = new WorkflowState
        {
            WorkflowId = "wf-md-del",
            WorkflowStatus = "not_started",
            WorkflowStarted = DateTime.UtcNow
        };
        await _sut.SaveAsync(state);

        File.Exists(GetJsonPath("wf-md-del")).Should().BeTrue();
        File.Exists(GetMarkdownPath("wf-md-del")).Should().BeTrue();

        var deleted = await _sut.DeleteAsync("wf-md-del");

        deleted.Should().BeTrue();
        File.Exists(GetJsonPath("wf-md-del")).Should().BeFalse();
        File.Exists(GetMarkdownPath("wf-md-del")).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_NoMarkdownFile_StillSucceeds()
    {
        // Create directly via inner (no markdown)
        await _inner.CreateAsync("wf-md-noclean");

        var deleted = await _sut.DeleteAsync("wf-md-noclean");

        deleted.Should().BeTrue();
    }

    // ── Delegation passthrough tests ─────────────────────────────────────────

    [Fact]
    public async Task GetNodeStateAsync_DelegatesToInner()
    {
        var state = await _sut.CreateAsync("wf-md-node");
        state.Nodes["n1"] = new NodeState { NodeId = "n1", Status = "in_progress" };
        await _sut.SaveAsync(state);

        var node = await _sut.GetNodeStateAsync("wf-md-node", "n1");

        node.Should().NotBeNull();
        node!.Status.Should().Be("in_progress");
    }

    [Fact]
    public async Task UpdateNodeStateAsync_DelegatesToInner()
    {
        var state = await _sut.CreateAsync("wf-md-upd");
        state.Nodes["n1"] = new NodeState { NodeId = "n1", Status = "not_started" };
        await _sut.SaveAsync(state);

        await _sut.UpdateNodeStateAsync("wf-md-upd", "n1",
            new NodeState { NodeId = "n1", Status = "completed" });

        var updated = await _sut.GetNodeStateAsync("wf-md-upd", "n1");
        updated!.Status.Should().Be("completed");
    }

    [Fact]
    public async Task CompleteWorkflowAsync_DelegatesToInner()
    {
        await _sut.CreateAsync("wf-md-compl");

        await _sut.CompleteWorkflowAsync("wf-md-compl");

        var status = await _sut.GetWorkflowStatusAsync("wf-md-compl");
        status.Should().Be("completed");
    }

    // ── Path validation ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("../escape")]
    [InlineData("path/traversal")]
    public async Task DeleteAsync_TraversalInId_ThrowsArgumentException(string workflowId)
    {
        var act = () => _sut.DeleteAsync(workflowId);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── Markdown content with nodes and metadata ─────────────────────────────

    [Fact]
    public async Task SaveAsync_WithNodesAndMetadata_MarkdownIncludesAllDetails()
    {
        var state = new WorkflowState
        {
            WorkflowId = "wf-md-detail",
            WorkflowStatus = "in_progress",
            CurrentNodeId = "phase-0",
            WorkflowStarted = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc)
        };
        state.Nodes["phase-0"] = new NodeState
        {
            NodeId = "phase-0",
            NodeType = "phase",
            Status = "in_progress",
            StartedAt = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            Iteration = 2,
            Metadata = new Dictionary<string, object>
            {
                ["score"] = 85,
                ["label"] = "Discovery Phase"
            }
        };

        await _sut.SaveAsync(state);

        var md = await File.ReadAllTextAsync(GetMarkdownPath("wf-md-detail"));
        md.Should().Contain("# Workflow State: wf-md-detail");
        md.Should().Contain("### phase-0 (phase)");
        md.Should().Contain("- **Status**: in_progress");
        md.Should().Contain("- **Iteration**: 2");
        md.Should().Contain("score:");
        md.Should().Contain("label:");
    }
}

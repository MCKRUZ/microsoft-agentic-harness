using Domain.Common.Config.Infrastructure;
using Domain.Common.Workflow;
using FluentAssertions;
using Infrastructure.AI.Generators;
using Infrastructure.AI.StateManagement;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.StateManagement;

/// <summary>
/// Integration tests for <see cref="CompositeStateManager"/> verifying delegation
/// through the decorator stack with both markdown enabled and disabled.
/// Uses real temp directory for file I/O.
/// </summary>
public sealed class CompositeStateManagerTests : IDisposable
{
    private readonly string _basePath;

    public CompositeStateManagerTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"csm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_basePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
    }

    private CompositeStateManager CreateSut(bool enableMarkdown = true)
    {
        var config = new InfrastructureConfig
        {
            StateManagement = new StateManagementConfig
            {
                BasePath = _basePath,
                EnableMarkdownGeneration = enableMarkdown
            }
        };
        var optionsMonitor = Mock.Of<IOptionsMonitor<InfrastructureConfig>>(
            m => m.CurrentValue == config);

        return new CompositeStateManager(
            NullLogger<CompositeStateManager>.Instance,
            new StateMarkdownGenerator(),
            optionsMonitor);
    }

    // ── Constructor with inner manager ───────────────────────────────────────

    private CompositeStateManager CreateSutWithInner(IStateManager inner, bool enableMarkdown = true)
    {
        var config = new InfrastructureConfig
        {
            StateManagement = new StateManagementConfig
            {
                BasePath = _basePath,
                EnableMarkdownGeneration = enableMarkdown
            }
        };
        var optionsMonitor = Mock.Of<IOptionsMonitor<InfrastructureConfig>>(
            m => m.CurrentValue == config);

        return new CompositeStateManager(
            inner,
            NullLogger<CompositeStateManager>.Instance,
            new StateMarkdownGenerator(),
            optionsMonitor);
    }

    // ── CRUD with markdown enabled ───────────────────────────────────────────

    [Fact]
    public async Task CreateAndSave_MarkdownEnabled_CreatesJsonAndMarkdown()
    {
        var sut = CreateSut(enableMarkdown: true);

        // CreateAsync delegates to inner which saves JSON directly.
        // Markdown is only generated when the decorator's SaveAsync is invoked.
        var state = await sut.CreateAsync("wf-md");
        state.WorkflowId.Should().Be("wf-md");

        // Explicit save triggers markdown generation through the decorator
        await sut.SaveAsync(state);

        // JSON file should exist
        var jsonPath = Path.Combine(_basePath, "wf-md", "checkpoints", "workflow-state.json");
        File.Exists(jsonPath).Should().BeTrue();

        // Markdown file should exist after explicit save
        var mdPath = Path.Combine(_basePath, "wf-md", "inputs", "workflow-state.md");
        File.Exists(mdPath).Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_MarkdownEnabled_JsonCreatedButNotMarkdown()
    {
        // CreateAsync goes through inner's CreateAsync which calls inner's SaveAsync,
        // bypassing the decorator. This is expected decorator behavior.
        var sut = CreateSut(enableMarkdown: true);

        await sut.CreateAsync("wf-create-only");

        // JSON file exists (created by inner)
        var jsonPath = Path.Combine(_basePath, "wf-create-only", "checkpoints", "workflow-state.json");
        File.Exists(jsonPath).Should().BeTrue();

        // Markdown is NOT created yet - requires explicit SaveAsync through decorator
        var mdPath = Path.Combine(_basePath, "wf-create-only", "inputs", "workflow-state.md");
        File.Exists(mdPath).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_MarkdownEnabled_UpdatesBothFiles()
    {
        var sut = CreateSut(enableMarkdown: true);
        var state = await sut.CreateAsync("wf-save-md");

        state.WorkflowStatus = "in_progress";
        state.CurrentNodeId = "phase-1";
        await sut.SaveAsync(state);

        // Verify JSON reflects the update
        var loaded = await sut.LoadAsync("wf-save-md");
        loaded!.WorkflowStatus.Should().Be("in_progress");

        // Verify markdown file was updated
        var mdPath = Path.Combine(_basePath, "wf-save-md", "inputs", "workflow-state.md");
        var mdContent = await File.ReadAllTextAsync(mdPath);
        mdContent.Should().Contain("in_progress");
        mdContent.Should().Contain("phase-1");
    }

    // ── CRUD with markdown disabled ──────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_MarkdownDisabled_CreatesOnlyJson()
    {
        var sut = CreateSut(enableMarkdown: false);

        await sut.CreateAsync("wf-nomd");

        // JSON file should exist
        var jsonPath = Path.Combine(_basePath, "wf-nomd", "checkpoints", "workflow-state.json");
        File.Exists(jsonPath).Should().BeTrue();

        // Markdown file should NOT exist
        var mdPath = Path.Combine(_basePath, "wf-nomd", "inputs", "workflow-state.md");
        File.Exists(mdPath).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_MarkdownDisabled_NoMarkdownGenerated()
    {
        var sut = CreateSut(enableMarkdown: false);
        var state = await sut.CreateAsync("wf-nomd-save");

        state.WorkflowStatus = "in_progress";
        await sut.SaveAsync(state);

        var mdPath = Path.Combine(_basePath, "wf-nomd-save", "inputs", "workflow-state.md");
        File.Exists(mdPath).Should().BeFalse();
    }

    // ── Full lifecycle test ──────────────────────────────────────────────────

    [Fact]
    public async Task FullLifecycle_CreateLoadUpdateDelete()
    {
        var sut = CreateSut(enableMarkdown: true);

        // Create
        var state = await sut.CreateAsync("wf-lifecycle");
        (await sut.ExistsAsync("wf-lifecycle")).Should().BeTrue();

        // Update with nodes
        state.Nodes["n1"] = new NodeState { NodeId = "n1", NodeType = "phase", Status = "not_started" };
        state.CurrentNodeId = "n1";
        await sut.SaveAsync(state);

        // Node operations via delegation
        var node = await sut.GetNodeStateAsync("wf-lifecycle", "n1");
        node.Should().NotBeNull();
        node!.NodeType.Should().Be("phase");

        // Transition
        await sut.TransitionAsync("wf-lifecycle", "n1", "in_progress");
        var updated = await sut.GetNodeStateAsync("wf-lifecycle", "n1");
        updated!.Status.Should().Be("in_progress");

        // Metadata
        await sut.SetMetadataAsync("wf-lifecycle", "n1", "score", 42);
        var meta = await sut.GetAllMetadataAsync("wf-lifecycle", "n1");
        meta.Should().ContainKey("score");

        // Completion queries
        var incomplete = await sut.GetIncompleteNodesAsync("wf-lifecycle");
        incomplete.Should().ContainSingle();

        await sut.TransitionAsync("wf-lifecycle", "n1", "completed");
        var completed = await sut.GetCompletedNodesAsync("wf-lifecycle");
        completed.Should().ContainSingle();

        // Workflow status
        await sut.SetWorkflowStatusAsync("wf-lifecycle", "completed");
        var status = await sut.GetWorkflowStatusAsync("wf-lifecycle");
        status.Should().Be("completed");

        // Delete
        var deleted = await sut.DeleteAsync("wf-lifecycle");
        deleted.Should().BeTrue();
        (await sut.ExistsAsync("wf-lifecycle")).Should().BeFalse();
    }

    // ── Constructor with pre-configured inner manager ────────────────────────

    [Fact]
    public async Task Constructor_WithInner_DelegatesCorrectly()
    {
        var mockInner = new Mock<IStateManager>();
        mockInner.Setup(m => m.LoadAsync("wf-inner", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowState { WorkflowId = "wf-inner", WorkflowStatus = "in_progress" });

        var sut = CreateSutWithInner(mockInner.Object, enableMarkdown: false);

        var loaded = await sut.LoadAsync("wf-inner");

        loaded.Should().NotBeNull();
        loaded!.WorkflowId.Should().Be("wf-inner");
        mockInner.Verify(m => m.LoadAsync("wf-inner", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Constructor_WithInner_MarkdownEnabled_WrapsWithDecorator()
    {
        var mockInner = new Mock<IStateManager>();
        mockInner.Setup(m => m.SaveAsync(It.IsAny<WorkflowState>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSutWithInner(mockInner.Object, enableMarkdown: true);

        var state = new WorkflowState
        {
            WorkflowId = "wf-decorated",
            WorkflowStatus = "in_progress",
            WorkflowStarted = DateTime.UtcNow
        };
        await sut.SaveAsync(state);

        // Inner's SaveAsync should have been called (through the decorator)
        mockInner.Verify(m => m.SaveAsync(It.IsAny<WorkflowState>(), It.IsAny<CancellationToken>()), Times.Once);

        // Markdown file should be generated by the decorator
        var mdPath = Path.Combine(_basePath, "wf-decorated", "inputs", "workflow-state.md");
        File.Exists(mdPath).Should().BeTrue();
    }

    // ── Delegation passthrough tests ─────────────────────────────────────────

    [Fact]
    public async Task CanTransitionAsync_DelegatesToInner()
    {
        var sut = CreateSut(enableMarkdown: false);
        var state = await sut.CreateAsync("wf-cantrans");
        state.Nodes["n1"] = new NodeState { NodeId = "n1", Status = "not_started" };
        await sut.SaveAsync(state);

        var can = await sut.CanTransitionAsync("wf-cantrans", "n1", "in_progress");

        can.Should().BeTrue();
    }

    [Fact]
    public async Task SetCurrentNodeAsync_DelegatesToInner()
    {
        var sut = CreateSut(enableMarkdown: false);
        await sut.CreateAsync("wf-setcur");

        await sut.SetCurrentNodeAsync("wf-setcur", "node-x");

        var loaded = await sut.LoadAsync("wf-setcur");
        loaded!.CurrentNodeId.Should().Be("node-x");
    }

    [Fact]
    public async Task CompleteWorkflowAsync_DelegatesToInner()
    {
        var sut = CreateSut(enableMarkdown: false);
        await sut.CreateAsync("wf-compl");

        await sut.CompleteWorkflowAsync("wf-compl");

        var status = await sut.GetWorkflowStatusAsync("wf-compl");
        status.Should().Be("completed");
    }
}

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
/// Integration tests for <see cref="CompositeStateManager"/> verifying the decorator
/// chain creates both JSON checkpoints and markdown files when enabled, and skips
/// markdown generation when disabled.
/// </summary>
public sealed class CompositeStateManagerIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public CompositeStateManagerIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"composite-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private CompositeStateManager CreateSut(bool enableMarkdown = true)
    {
        var config = new InfrastructureConfig
        {
            StateManagement = new StateManagementConfig
            {
                BasePath = _tempDir,
                EnableMarkdownGeneration = enableMarkdown
            }
        };
        var options = Mock.Of<IOptionsMonitor<InfrastructureConfig>>(
            o => o.CurrentValue == config);

        var markdownGenerator = new StateMarkdownGenerator();

        return new CompositeStateManager(
            NullLogger<CompositeStateManager>.Instance,
            markdownGenerator,
            options);
    }

    [Fact]
    public async Task SaveAsync_WithMarkdown_CreatesBothFiles()
    {
        var sut = CreateSut(enableMarkdown: true);

        var state = await sut.CreateAsync("wf-both");
        state.WorkflowId.Should().Be("wf-both");

        // CreateAsync delegates to inner.CreateAsync -> inner.SaveAsync (JSON only).
        // Markdown is generated on the decorator's SaveAsync path.
        await sut.SaveAsync(state);

        var jsonPath = Path.Combine(_tempDir, "wf-both", "checkpoints", "workflow-state.json");
        File.Exists(jsonPath).Should().BeTrue();

        var mdPath = Path.Combine(_tempDir, "wf-both", "inputs", "workflow-state.md");
        File.Exists(mdPath).Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_WithoutMarkdown_OnlyCreatesJson()
    {
        var sut = CreateSut(enableMarkdown: false);

        await sut.CreateAsync("wf-json-only");

        var jsonPath = Path.Combine(_tempDir, "wf-json-only", "checkpoints", "workflow-state.json");
        File.Exists(jsonPath).Should().BeTrue();

        var mdPath = Path.Combine(_tempDir, "wf-json-only", "inputs", "workflow-state.md");
        File.Exists(mdPath).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_UpdatesMarkdown()
    {
        var sut = CreateSut(enableMarkdown: true);
        var state = await sut.CreateAsync("wf-update");

        state.WorkflowStatus = "in_progress";
        await sut.SaveAsync(state);

        var mdPath = Path.Combine(_tempDir, "wf-update", "inputs", "workflow-state.md");
        var markdown = await File.ReadAllTextAsync(mdPath);
        markdown.Should().Contain("in_progress");
    }

    [Fact]
    public async Task LoadAsync_ReturnsPersistedState()
    {
        var sut = CreateSut();
        await sut.CreateAsync("wf-load");

        var loaded = await sut.LoadAsync("wf-load");

        loaded.Should().NotBeNull();
        loaded!.WorkflowId.Should().Be("wf-load");
    }

    [Fact]
    public async Task ExistsAsync_AfterCreate_ReturnsTrue()
    {
        var sut = CreateSut();
        await sut.CreateAsync("wf-exists");

        var exists = await sut.ExistsAsync("wf-exists");

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_RemovesState()
    {
        var sut = CreateSut(enableMarkdown: true);
        var state = await sut.CreateAsync("wf-delete");
        await sut.SaveAsync(state); // generates markdown
        (await sut.ExistsAsync("wf-delete")).Should().BeTrue();

        await sut.DeleteAsync("wf-delete");

        (await sut.ExistsAsync("wf-delete")).Should().BeFalse();
    }

    [Fact]
    public async Task NodeOperations_DelegateToInnerChain()
    {
        var sut = CreateSut();
        await sut.CreateAsync("wf-nodes");

        var node = new NodeState
        {
            NodeId = "phase-0",
            NodeType = "phase",
            Status = "not_started"
        };
        await sut.UpdateNodeStateAsync("wf-nodes", "phase-0", node);

        var loaded = await sut.GetNodeStateAsync("wf-nodes", "phase-0");
        loaded.Should().NotBeNull();
        loaded!.NodeId.Should().Be("phase-0");

        var byType = await sut.GetNodesByTypeAsync("wf-nodes", "phase");
        byType.Should().ContainSingle();
    }

    [Fact]
    public async Task TransitionAndComplete_FullLifecycle()
    {
        var sut = CreateSut();
        await sut.CreateAsync("wf-lifecycle");
        await sut.UpdateNodeStateAsync("wf-lifecycle", "n1",
            new NodeState { NodeId = "n1", NodeType = "phase", Status = "not_started" });

        await sut.SetCurrentNodeAsync("wf-lifecycle", "n1");
        await sut.TransitionAsync("wf-lifecycle", "n1", "in_progress");
        await sut.TransitionAsync("wf-lifecycle", "n1", "completed");

        var isComplete = await sut.IsNodeCompleteAsync("wf-lifecycle", "n1");
        isComplete.Should().BeTrue();

        await sut.CompleteWorkflowAsync("wf-lifecycle");
        var status = await sut.GetWorkflowStatusAsync("wf-lifecycle");
        status.Should().Be("completed");
    }

    [Fact]
    public async Task MetadataOperations_DelegateCorrectly()
    {
        var sut = CreateSut();
        await sut.CreateAsync("wf-meta");
        await sut.UpdateNodeStateAsync("wf-meta", "n1",
            new NodeState { NodeId = "n1", NodeType = "phase", Status = "not_started" });

        await sut.SetMetadataAsync("wf-meta", "n1", "key", "value");

        var all = await sut.GetAllMetadataAsync("wf-meta", "n1");
        all.Should().ContainKey("key");
    }

    [Fact]
    public async Task GetIncompleteAndCompleted_DelegateCorrectly()
    {
        var sut = CreateSut();
        await sut.CreateAsync("wf-lists");
        await sut.UpdateNodeStateAsync("wf-lists", "done",
            new NodeState { NodeId = "done", NodeType = "phase", Status = "completed" });
        await sut.UpdateNodeStateAsync("wf-lists", "pending",
            new NodeState { NodeId = "pending", NodeType = "phase", Status = "in_progress" });

        var incomplete = await sut.GetIncompleteNodesAsync("wf-lists");
        incomplete.Should().ContainSingle(n => n.NodeId == "pending");

        var completed = await sut.GetCompletedNodesAsync("wf-lists");
        completed.Should().ContainSingle(n => n.NodeId == "done");
    }

    [Fact]
    public async Task SaveAsync_WithoutMarkdown_SkipsMarkdownGeneration()
    {
        var sut = CreateSut(enableMarkdown: false);
        var state = await sut.CreateAsync("wf-no-md");

        await sut.SaveAsync(state);

        var mdPath = Path.Combine(_tempDir, "wf-no-md", "inputs", "workflow-state.md");
        File.Exists(mdPath).Should().BeFalse();
    }
}

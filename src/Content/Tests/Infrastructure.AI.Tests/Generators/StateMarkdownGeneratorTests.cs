using Domain.Common.Workflow;
using FluentAssertions;
using Infrastructure.AI.Generators;
using Xunit;

namespace Infrastructure.AI.Tests.Generators;

/// <summary>
/// Tests for <see cref="StateMarkdownGenerator"/> covering YAML frontmatter,
/// markdown body rendering, node formatting, and metadata display.
/// </summary>
public sealed class StateMarkdownGeneratorTests
{
    private readonly StateMarkdownGenerator _sut = new();

    [Fact]
    public void Generate_MinimalState_IncludesYamlFrontmatter()
    {
        var state = new WorkflowState
        {
            WorkflowId = "proj-001",
            CurrentNodeId = "phase0",
            WorkflowStatus = "not_started",
            WorkflowStarted = new DateTime(2025, 1, 10, 9, 0, 0, DateTimeKind.Utc)
        };

        var result = _sut.Generate(state);

        result.Should().StartWith("---");
        result.Should().Contain("workflow_id: proj-001");
        result.Should().Contain("current_node_id: phase0");
        result.Should().Contain("workflow_status: not_started");
        result.Should().Contain("workflow_started: 2025-01-10T09:00:00Z");
        result.Should().Contain("---");
    }

    [Fact]
    public void Generate_WithCompletedDate_IncludesCompletionInFrontmatter()
    {
        var state = new WorkflowState
        {
            WorkflowId = "proj-001",
            WorkflowStatus = "completed",
            WorkflowStarted = new DateTime(2025, 1, 10, 9, 0, 0, DateTimeKind.Utc),
            WorkflowCompleted = new DateTime(2025, 1, 12, 17, 0, 0, DateTimeKind.Utc)
        };

        var result = _sut.Generate(state);

        result.Should().Contain("workflow_completed: 2025-01-12T17:00:00Z");
    }

    [Fact]
    public void Generate_WithoutCompletedDate_OmitsCompletionLine()
    {
        var state = new WorkflowState
        {
            WorkflowId = "proj-001",
            WorkflowStatus = "in_progress",
            WorkflowStarted = new DateTime(2025, 1, 10, 9, 0, 0, DateTimeKind.Utc)
        };

        var result = _sut.Generate(state);

        result.Should().NotContain("workflow_completed");
        result.Should().NotContain("**Completed**");
    }

    [Fact]
    public void Generate_IncludesMarkdownHeader()
    {
        var state = new WorkflowState { WorkflowId = "my-workflow" };

        var result = _sut.Generate(state);

        result.Should().Contain("# Workflow State: my-workflow");
        result.Should().Contain("**Current Node**:");
        result.Should().Contain("**Status**:");
    }

    [Fact]
    public void Generate_WithNodes_RendersNodeSections()
    {
        var state = new WorkflowState
        {
            WorkflowId = "proj-001",
            WorkflowStarted = DateTime.UtcNow
        };

        state.Nodes["discovery"] = new NodeState
        {
            NodeId = "discovery",
            NodeType = "phase",
            Status = "completed",
            Iteration = 1,
            StartedAt = new DateTime(2025, 1, 10, 9, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2025, 1, 10, 10, 0, 0, DateTimeKind.Utc)
        };

        var result = _sut.Generate(state);

        result.Should().Contain("## Nodes");
        result.Should().Contain("### discovery (phase)");
        result.Should().Contain("- **Status**: completed");
        result.Should().Contain("- **Started**: 2025-01-10T09:00:00Z");
        result.Should().Contain("- **Completed**: 2025-01-10T10:00:00Z");
        result.Should().Contain("- **Iteration**: 1");
    }

    [Fact]
    public void Generate_NodeWithMetadata_RendersMetadata()
    {
        var state = new WorkflowState
        {
            WorkflowId = "proj-001",
            WorkflowStarted = DateTime.UtcNow
        };

        var node = new NodeState
        {
            NodeId = "analysis",
            NodeType = "skill",
            Status = "in_progress",
            Iteration = 2
        };
        node.Metadata["coverage"] = "85%";
        node.Metadata["passed"] = true;
        state.Nodes["analysis"] = node;

        var result = _sut.Generate(state);

        result.Should().Contain("- **Metadata**:");
        result.Should().Contain("coverage: \"85%\"");
        result.Should().Contain("passed: true");
    }

    [Fact]
    public void Generate_NoNodes_OmitsNodesSection()
    {
        var state = new WorkflowState { WorkflowId = "empty" };

        var result = _sut.Generate(state);

        result.Should().NotContain("## Nodes");
    }

    [Fact]
    public void Generate_NodeWithoutTimestamps_OmitsTimestampLines()
    {
        var state = new WorkflowState
        {
            WorkflowId = "proj-001",
            WorkflowStarted = DateTime.UtcNow
        };

        state.Nodes["pending"] = new NodeState
        {
            NodeId = "pending",
            NodeType = "gate",
            Status = "not_started",
            Iteration = 1
        };

        var result = _sut.Generate(state);

        result.Should().NotContain("- **Started**:");
        result.Should().NotContain("- **Completed**:");
    }

    [Fact]
    public void Generate_MetadataNullValue_RendersNull()
    {
        var state = new WorkflowState
        {
            WorkflowId = "proj-001",
            WorkflowStarted = DateTime.UtcNow
        };

        var node = new NodeState { NodeId = "n1", Iteration = 1 };
        node.Metadata["key"] = null!;
        state.Nodes["n1"] = node;

        var result = _sut.Generate(state);

        result.Should().Contain("key: null");
    }

    [Fact]
    public void Generate_MetadataIntegerValue_RendersAsString()
    {
        var state = new WorkflowState
        {
            WorkflowId = "proj-001",
            WorkflowStarted = DateTime.UtcNow
        };

        var node = new NodeState { NodeId = "n1", Iteration = 1 };
        node.Metadata["count"] = 42;
        state.Nodes["n1"] = node;

        var result = _sut.Generate(state);

        result.Should().Contain("count: 42");
    }

    [Fact]
    public void Generate_MultipleNodes_OrdersByStartedAt()
    {
        var state = new WorkflowState
        {
            WorkflowId = "proj-001",
            WorkflowStarted = DateTime.UtcNow
        };

        state.Nodes["second"] = new NodeState
        {
            NodeId = "second",
            NodeType = "skill",
            Iteration = 1,
            StartedAt = new DateTime(2025, 1, 10, 10, 0, 0, DateTimeKind.Utc)
        };

        state.Nodes["first"] = new NodeState
        {
            NodeId = "first",
            NodeType = "phase",
            Iteration = 1,
            StartedAt = new DateTime(2025, 1, 10, 9, 0, 0, DateTimeKind.Utc)
        };

        var result = _sut.Generate(state);

        var firstIdx = result.IndexOf("### first");
        var secondIdx = result.IndexOf("### second");
        firstIdx.Should().BeLessThan(secondIdx);
    }
}

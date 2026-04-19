using Domain.Common.Workflow;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Workflow;

/// <summary>
/// Tests for <see cref="WorkflowState"/> — node management, queries, metadata,
/// and defaults.
/// </summary>
public class WorkflowStateTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var state = new WorkflowState();

        state.WorkflowId.Should().BeEmpty();
        state.CurrentNodeId.Should().BeEmpty();
        state.WorkflowStatus.Should().Be("not_started");
        state.WorkflowCompleted.Should().BeNull();
        state.Nodes.Should().BeEmpty();
        state.Metadata.Should().BeEmpty();
    }

    [Fact]
    public void GetOrCreateNode_NewNode_CreatesIt()
    {
        var state = new WorkflowState();

        var node = state.GetOrCreateNode("node-1");

        node.NodeId.Should().Be("node-1");
        state.Nodes.Should().ContainKey("node-1");
    }

    [Fact]
    public void GetOrCreateNode_ExistingNode_ReturnsSame()
    {
        var state = new WorkflowState();
        var first = state.GetOrCreateNode("node-1");
        first.Status = "in_progress";

        var second = state.GetOrCreateNode("node-1");

        second.Should().BeSameAs(first);
        second.Status.Should().Be("in_progress");
    }

    [Fact]
    public void GetNodesByType_ReturnsMatchingNodes()
    {
        var state = new WorkflowState();
        state.Nodes["a"] = new NodeState { NodeId = "a", NodeType = "skill" };
        state.Nodes["b"] = new NodeState { NodeId = "b", NodeType = "phase" };
        state.Nodes["c"] = new NodeState { NodeId = "c", NodeType = "skill" };

        var skills = state.GetNodesByType("skill");

        skills.Should().HaveCount(2);
        skills.Select(n => n.NodeId).Should().Contain(["a", "c"]);
    }

    [Fact]
    public void GetNodesByStatus_ReturnsMatchingNodes()
    {
        var state = new WorkflowState();
        state.Nodes["a"] = new NodeState { NodeId = "a", Status = "completed" };
        state.Nodes["b"] = new NodeState { NodeId = "b", Status = "in_progress" };
        state.Nodes["c"] = new NodeState { NodeId = "c", Status = "completed" };

        var completed = state.GetNodesByStatus("completed");

        completed.Should().HaveCount(2);
    }

    [Fact]
    public void GetIncompleteNodes_ReturnsNonCompletedNodes()
    {
        var state = new WorkflowState();
        state.Nodes["a"] = new NodeState { NodeId = "a", Status = "completed" };
        state.Nodes["b"] = new NodeState { NodeId = "b", Status = "in_progress" };
        state.Nodes["c"] = new NodeState { NodeId = "c", Status = "not_started" };

        var incomplete = state.GetIncompleteNodes();

        incomplete.Should().HaveCount(2);
        incomplete.Select(n => n.NodeId).Should().Contain(["b", "c"]);
    }

    [Fact]
    public void GetCompletedNodes_ReturnsCompletedOnly()
    {
        var state = new WorkflowState();
        state.Nodes["a"] = new NodeState { NodeId = "a", Status = "completed" };
        state.Nodes["b"] = new NodeState { NodeId = "b", Status = "in_progress" };

        var completed = state.GetCompletedNodes();

        completed.Should().ContainSingle().Which.NodeId.Should().Be("a");
    }

    [Fact]
    public void IsNodeComplete_ExistingCompleted_ReturnsTrue()
    {
        var state = new WorkflowState();
        state.Nodes["a"] = new NodeState { NodeId = "a", Status = "completed" };

        state.IsNodeComplete("a").Should().BeTrue();
    }

    [Fact]
    public void IsNodeComplete_ExistingNotCompleted_ReturnsFalse()
    {
        var state = new WorkflowState();
        state.Nodes["a"] = new NodeState { NodeId = "a", Status = "in_progress" };

        state.IsNodeComplete("a").Should().BeFalse();
    }

    [Fact]
    public void IsNodeComplete_NonExistent_ReturnsFalse()
    {
        var state = new WorkflowState();

        state.IsNodeComplete("missing").Should().BeFalse();
    }

    [Fact]
    public void GetTotalIterationCount_SumsAllNodeIterations()
    {
        var state = new WorkflowState();
        state.Nodes["a"] = new NodeState { NodeId = "a", Iteration = 2 };
        state.Nodes["b"] = new NodeState { NodeId = "b", Iteration = 3 };

        state.GetTotalIterationCount().Should().Be(5);
    }

    [Fact]
    public void GetNodeMetadata_ExistingKey_ReturnsValue()
    {
        var state = new WorkflowState();
        state.Nodes["a"] = new NodeState { NodeId = "a" };
        state.Nodes["a"].Metadata["score"] = 85;

        state.GetNodeMetadata<int>("a", "score").Should().Be(85);
    }

    [Fact]
    public void GetNodeMetadata_MissingNode_ReturnsDefault()
    {
        var state = new WorkflowState();

        state.GetNodeMetadata<int>("missing", "score").Should().Be(0);
    }

    [Fact]
    public void GetNodeMetadata_MissingKey_ReturnsDefault()
    {
        var state = new WorkflowState();
        state.Nodes["a"] = new NodeState { NodeId = "a" };

        state.GetNodeMetadata<int>("a", "missing").Should().Be(0);
    }

    [Fact]
    public void SetNodeMetadata_CreatesNodeIfMissing()
    {
        var state = new WorkflowState();

        state.SetNodeMetadata("new-node", "key", "value");

        state.Nodes.Should().ContainKey("new-node");
        state.Nodes["new-node"].Metadata["key"].Should().Be("value");
    }
}

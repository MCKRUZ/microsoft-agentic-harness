using Domain.Common.Workflow;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Workflow;

/// <summary>
/// Tests for <see cref="WorkflowState"/> logic methods: GetOrCreateNode, GetNodesByType,
/// GetNodesByStatus, GetIncompleteNodes, GetCompletedNodes, IsNodeComplete,
/// GetTotalIterationCount, GetNodeMetadata, SetNodeMetadata.
/// </summary>
public class WorkflowStateLogicTests
{
    private static WorkflowState CreateWorkflowWithNodes()
    {
        var state = new WorkflowState { WorkflowId = "test-001" };
        state.Nodes["phase1"] = new NodeState { NodeId = "phase1", NodeType = "phase", Status = "completed" };
        state.Nodes["phase2"] = new NodeState { NodeId = "phase2", NodeType = "phase", Status = "in_progress" };
        state.Nodes["skill1"] = new NodeState { NodeId = "skill1", NodeType = "skill", Status = "not_started" };
        state.Nodes["skill2"] = new NodeState { NodeId = "skill2", NodeType = "skill", Status = "completed", Iteration = 3 };
        return state;
    }

    // ── GetOrCreateNode ──

    [Fact]
    public void GetOrCreateNode_ExistingNode_ReturnsSameNode()
    {
        var state = CreateWorkflowWithNodes();

        var node = state.GetOrCreateNode("phase1");

        node.NodeId.Should().Be("phase1");
        node.Status.Should().Be("completed");
    }

    [Fact]
    public void GetOrCreateNode_NewNode_CreatesAndReturns()
    {
        var state = new WorkflowState();

        var node = state.GetOrCreateNode("new-node");

        node.NodeId.Should().Be("new-node");
        state.Nodes.Should().ContainKey("new-node");
    }

    [Fact]
    public void GetOrCreateNode_NewNode_HasDefaultStatus()
    {
        var state = new WorkflowState();

        var node = state.GetOrCreateNode("new-node");

        node.Status.Should().Be("not_started");
    }

    // ── GetNodesByType ──

    [Fact]
    public void GetNodesByType_ReturnsMatchingNodes()
    {
        var state = CreateWorkflowWithNodes();

        var phases = state.GetNodesByType("phase");

        phases.Should().HaveCount(2);
        phases.Select(n => n.NodeId).Should().Contain(new[] { "phase1", "phase2" });
    }

    [Fact]
    public void GetNodesByType_NoMatch_ReturnsEmpty()
    {
        var state = CreateWorkflowWithNodes();

        state.GetNodesByType("gate").Should().BeEmpty();
    }

    // ── GetNodesByStatus ──

    [Fact]
    public void GetNodesByStatus_ReturnsMatchingNodes()
    {
        var state = CreateWorkflowWithNodes();

        var completed = state.GetNodesByStatus("completed");

        completed.Should().HaveCount(2);
    }

    [Fact]
    public void GetNodesByStatus_NoMatch_ReturnsEmpty()
    {
        var state = CreateWorkflowWithNodes();

        state.GetNodesByStatus("failed").Should().BeEmpty();
    }

    // ── GetIncompleteNodes ──

    [Fact]
    public void GetIncompleteNodes_ReturnsNonCompletedNodes()
    {
        var state = CreateWorkflowWithNodes();

        var incomplete = state.GetIncompleteNodes();

        incomplete.Should().HaveCount(2);
        incomplete.Select(n => n.NodeId).Should().Contain(new[] { "phase2", "skill1" });
    }

    // ── GetCompletedNodes ──

    [Fact]
    public void GetCompletedNodes_ReturnsCompletedNodes()
    {
        var state = CreateWorkflowWithNodes();

        var completed = state.GetCompletedNodes();

        completed.Should().HaveCount(2);
    }

    // ── IsNodeComplete ──

    [Fact]
    public void IsNodeComplete_CompletedNode_ReturnsTrue()
    {
        var state = CreateWorkflowWithNodes();

        state.IsNodeComplete("phase1").Should().BeTrue();
    }

    [Fact]
    public void IsNodeComplete_InProgressNode_ReturnsFalse()
    {
        var state = CreateWorkflowWithNodes();

        state.IsNodeComplete("phase2").Should().BeFalse();
    }

    [Fact]
    public void IsNodeComplete_NonExistentNode_ReturnsFalse()
    {
        var state = CreateWorkflowWithNodes();

        state.IsNodeComplete("nonexistent").Should().BeFalse();
    }

    // ── GetTotalIterationCount ──

    [Fact]
    public void GetTotalIterationCount_SumsAcrossAllNodes()
    {
        var state = CreateWorkflowWithNodes();
        // phase1=1, phase2=1, skill1=1, skill2=3 = 6
        state.GetTotalIterationCount().Should().Be(6);
    }

    [Fact]
    public void GetTotalIterationCount_EmptyWorkflow_ReturnsZero()
    {
        var state = new WorkflowState();

        state.GetTotalIterationCount().Should().Be(0);
    }

    // ── GetNodeMetadata ──

    [Fact]
    public void GetNodeMetadata_ExistingKey_ReturnsValue()
    {
        var state = CreateWorkflowWithNodes();
        state.Nodes["phase1"].Metadata["score"] = 95;

        var score = state.GetNodeMetadata<int>("phase1", "score");

        score.Should().Be(95);
    }

    [Fact]
    public void GetNodeMetadata_NonExistentNode_ReturnsDefault()
    {
        var state = CreateWorkflowWithNodes();

        state.GetNodeMetadata<int>("nonexistent", "score").Should().Be(0);
    }

    [Fact]
    public void GetNodeMetadata_NonExistentKey_ReturnsDefault()
    {
        var state = CreateWorkflowWithNodes();

        state.GetNodeMetadata<string>("phase1", "missing").Should().BeNull();
    }

    [Fact]
    public void GetNodeMetadata_TypeMismatch_AttemptsConversion()
    {
        var state = CreateWorkflowWithNodes();
        state.Nodes["phase1"].Metadata["score"] = 42;

        // int -> string via Convert.ChangeType
        var result = state.GetNodeMetadata<string>("phase1", "score");

        result.Should().Be("42");
    }

    [Fact]
    public void GetNodeMetadata_InconvertibleType_ReturnsDefault()
    {
        var state = CreateWorkflowWithNodes();
        state.Nodes["phase1"].Metadata["data"] = "not-a-number";

        // string "not-a-number" cannot convert to int
        var result = state.GetNodeMetadata<int>("phase1", "data");

        result.Should().Be(0);
    }

    // ── SetNodeMetadata ──

    [Fact]
    public void SetNodeMetadata_ExistingNode_SetsValue()
    {
        var state = CreateWorkflowWithNodes();

        state.SetNodeMetadata("phase1", "score", 99);

        state.Nodes["phase1"].Metadata["score"].Should().Be(99);
    }

    [Fact]
    public void SetNodeMetadata_NewNode_CreatesNodeAndSetsValue()
    {
        var state = new WorkflowState();

        state.SetNodeMetadata("new-node", "key", "value");

        state.Nodes.Should().ContainKey("new-node");
        state.Nodes["new-node"].Metadata["key"].Should().Be("value");
    }
}

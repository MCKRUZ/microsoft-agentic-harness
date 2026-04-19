using Domain.Common.Workflow;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Integration;

/// <summary>
/// Integration tests exercising a complete workflow lifecycle with real
/// <see cref="StateConfiguration"/>, <see cref="WorkflowState"/>, and
/// <see cref="NodeState"/> transitions end-to-end.
/// </summary>
public class WorkflowStateMachineIntegrationTests
{
    private static StateConfiguration CreateSdlcStateConfig() => new()
    {
        AllowedStatuses = ["not_started", "in_progress", "paused", "awaiting_input", "awaiting_approval", "completed", "failed", "skipped"],
        AllowedTransitions = new Dictionary<string, List<string>>
        {
            ["not_started"] = ["in_progress", "skipped"],
            ["in_progress"] = ["paused", "awaiting_input", "awaiting_approval", "completed", "failed"],
            ["paused"] = ["in_progress", "failed"],
            ["awaiting_input"] = ["in_progress", "failed"],
            ["awaiting_approval"] = ["in_progress", "completed", "failed"],
            ["completed"] = [],
            ["failed"] = ["not_started"],
            ["skipped"] = []
        },
        InitialStatus = "not_started",
        TerminalStates = ["completed", "skipped"]
    };

    [Fact]
    public void FullWorkflowLifecycle_DiscoveryToCompletion_AllTransitionsValid()
    {
        var config = CreateSdlcStateConfig();
        var workflow = new WorkflowState
        {
            WorkflowId = "proj-001",
            WorkflowStatus = "not_started"
        };

        // Phase 0: Discovery
        var discovery = workflow.GetOrCreateNode("phase0-discovery");
        discovery.NodeType = "phase";

        // Transition: not_started -> in_progress
        config.CanTransition(discovery.Status, "in_progress").Should().BeTrue();
        discovery.Status = "in_progress";
        discovery.StartedAt = DateTime.UtcNow;
        workflow.CurrentNodeId = "phase0-discovery";
        workflow.WorkflowStatus = "in_progress";

        // Add child skill nodes
        var intake = workflow.GetOrCreateNode("discovery-intake");
        intake.NodeType = "skill";
        config.CanTransition(intake.Status, "in_progress").Should().BeTrue();
        intake.Status = "in_progress";
        intake.StartedAt = DateTime.UtcNow;

        // Complete the intake skill
        config.CanTransition(intake.Status, "completed").Should().BeTrue();
        intake.Status = "completed";
        intake.CompletedAt = DateTime.UtcNow;
        intake.SetMetadata("token_count", 1500);

        // Validation gate
        var validation = workflow.GetOrCreateNode("discovery-validation");
        validation.NodeType = "validation";
        validation.Status = "in_progress";
        validation.SetMetadata("score", 92);
        validation.SetMetadata("critical_issues", 0);
        validation.Status = "completed";
        validation.CompletedAt = DateTime.UtcNow;

        // Complete the discovery phase
        discovery.Status = "completed";
        discovery.CompletedAt = DateTime.UtcNow;

        // Verify final state
        workflow.GetCompletedNodes().Should().HaveCount(3);
        workflow.GetIncompleteNodes().Should().BeEmpty();
        workflow.IsNodeComplete("phase0-discovery").Should().BeTrue();
        workflow.IsNodeComplete("discovery-intake").Should().BeTrue();
        workflow.IsNodeComplete("discovery-validation").Should().BeTrue();

        // Verify metadata survived transitions
        workflow.GetNodeMetadata<int>("discovery-intake", "token_count").Should().Be(1500);
        workflow.GetNodeMetadata<int>("discovery-validation", "score").Should().Be(92);
    }

    [Fact]
    public void FailureAndRetry_NodeFailsThenRetries_IterationIncrements()
    {
        var config = CreateSdlcStateConfig();
        var workflow = new WorkflowState { WorkflowId = "retry-test" };

        var skill = workflow.GetOrCreateNode("coding-task");
        skill.NodeType = "skill";
        skill.Status = "in_progress";
        skill.StartedAt = DateTime.UtcNow;

        // Fail the node
        config.CanTransition("in_progress", "failed").Should().BeTrue();
        skill.Status = "failed";
        skill.SetMetadata("error_message", "Build failed");

        // Retry: failed -> not_started -> in_progress
        config.CanTransition("failed", "not_started").Should().BeTrue();
        skill.Status = "not_started";
        skill.IncrementIteration();

        config.CanTransition("not_started", "in_progress").Should().BeTrue();
        skill.Status = "in_progress";
        skill.StartedAt = DateTime.UtcNow;

        // Complete on retry
        skill.Status = "completed";
        skill.CompletedAt = DateTime.UtcNow;

        skill.Iteration.Should().Be(2);
        skill.IsComplete().Should().BeTrue();
        skill.GetDuration().Should().NotBeNull();
    }

    [Fact]
    public void InvalidTransition_CompletedToInProgress_IsRejected()
    {
        var config = CreateSdlcStateConfig();

        config.CanTransition("completed", "in_progress").Should().BeFalse();
    }

    [Fact]
    public void IdempotentTransition_SameStatus_IsAllowed()
    {
        var config = CreateSdlcStateConfig();

        config.CanTransition("in_progress", "in_progress").Should().BeTrue();
    }

    [Fact]
    public void SkipNode_NotStartedToSkipped_AllowedAndTerminal()
    {
        var config = CreateSdlcStateConfig();

        config.CanTransition("not_started", "skipped").Should().BeTrue();
        config.IsTerminal("skipped").Should().BeTrue();
    }

    [Fact]
    public void MultiNodeWorkflow_GetTotalIterations_SumsCorrectly()
    {
        var workflow = new WorkflowState { WorkflowId = "iter-test" };

        var node1 = workflow.GetOrCreateNode("task-1");
        node1.Iteration = 1;

        var node2 = workflow.GetOrCreateNode("task-2");
        node2.Iteration = 3; // retried twice

        var node3 = workflow.GetOrCreateNode("task-3");
        node3.Iteration = 2; // retried once

        workflow.GetTotalIterationCount().Should().Be(6);
    }

    [Fact]
    public void NodeDuration_CompletedNode_ReturnsNonNullTimeSpan()
    {
        var start = DateTime.UtcNow.AddMinutes(-5);
        var end = DateTime.UtcNow;

        var node = new NodeState
        {
            NodeId = "timed-task",
            StartedAt = start,
            CompletedAt = end
        };

        var duration = node.GetDuration();

        duration.Should().NotBeNull();
        duration!.Value.Should().BeCloseTo(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void NodeDuration_NotStarted_ReturnsNull()
    {
        var node = new NodeState { NodeId = "unstarted" };

        node.GetDuration().Should().BeNull();
    }

    [Fact]
    public void StateConfigValidation_ValidConfig_ReturnsNoErrors()
    {
        var config = CreateSdlcStateConfig();

        var errors = config.Validate();

        // Only the "no default rule" warning may appear (it's about DecisionFramework, not StateConfig)
        errors.Where(e => !e.StartsWith("Warning")).Should().BeEmpty();
    }

    [Fact]
    public void StateConfigValidation_InvalidInitialStatus_ReturnsError()
    {
        var config = new StateConfiguration
        {
            AllowedStatuses = ["active", "done"],
            InitialStatus = "unknown_status",
            AllowedTransitions = new Dictionary<string, List<string>>
            {
                ["active"] = ["done"],
                ["done"] = []
            }
        };

        var errors = config.Validate();

        errors.Should().Contain(e => e.Contains("Initial status"));
    }

    [Fact]
    public void StateConfigValidation_TerminalStateWithTransitions_ReturnsError()
    {
        var config = new StateConfiguration
        {
            AllowedStatuses = ["active", "done"],
            InitialStatus = "active",
            TerminalStates = ["done"],
            AllowedTransitions = new Dictionary<string, List<string>>
            {
                ["active"] = ["done"],
                ["done"] = ["active"] // terminal with outgoing transition
            }
        };

        var errors = config.Validate();

        errors.Should().Contain(e => e.Contains("Terminal state") && e.Contains("done"));
    }

    [Fact]
    public void GetValidTransitions_ReturnsCorrectNextStates()
    {
        var config = CreateSdlcStateConfig();

        var transitions = config.GetValidTransitions("in_progress");

        transitions.Should().Contain("paused");
        transitions.Should().Contain("completed");
        transitions.Should().Contain("failed");
        transitions.Should().NotContain("not_started");
    }

    [Fact]
    public void GetValidTransitions_UnknownStatus_ReturnsEmpty()
    {
        var config = CreateSdlcStateConfig();

        config.GetValidTransitions("nonexistent").Should().BeEmpty();
    }

    [Fact]
    public void NodeActiveStates_InProgressAndAwaiting_AreActive()
    {
        var inProgress = new NodeState { Status = "in_progress" };
        var awaitingInput = new NodeState { Status = "awaiting_input" };
        var awaitingApproval = new NodeState { Status = "awaiting_approval" };
        var notStarted = new NodeState { Status = "not_started" };

        inProgress.IsActive().Should().BeTrue();
        awaitingInput.IsActive().Should().BeTrue();
        awaitingApproval.IsActive().Should().BeTrue();
        notStarted.IsActive().Should().BeFalse();
    }

    [Fact]
    public void NodeMetadata_GetTypedWithConversion_ConvertsCorrectly()
    {
        var node = new NodeState { NodeId = "meta-test" };
        node.SetMetadata("count", 42);

        // int to string conversion
        node.GetMetadata<string>("count").Should().Be("42");

        // Direct type match
        node.GetMetadata<int>("count").Should().Be(42);

        // Missing key with default
        node.GetMetadata("missing", 99).Should().Be(99);
    }

    [Fact]
    public void WorkflowState_FilterByTypeAndStatus_CombinedQueries()
    {
        var workflow = new WorkflowState { WorkflowId = "filter-test" };

        workflow.Nodes["p1"] = new NodeState { NodeId = "p1", NodeType = "phase", Status = "completed" };
        workflow.Nodes["p2"] = new NodeState { NodeId = "p2", NodeType = "phase", Status = "in_progress" };
        workflow.Nodes["s1"] = new NodeState { NodeId = "s1", NodeType = "skill", Status = "completed" };
        workflow.Nodes["s2"] = new NodeState { NodeId = "s2", NodeType = "skill", Status = "not_started" };
        workflow.Nodes["g1"] = new NodeState { NodeId = "g1", NodeType = "gate", Status = "completed" };

        var completedSkills = workflow.GetNodesByType("skill")
            .Where(n => n.IsComplete())
            .ToList();

        completedSkills.Should().ContainSingle().Which.NodeId.Should().Be("s1");

        var activePhases = workflow.GetNodesByType("phase")
            .Where(n => n.IsActive())
            .ToList();

        activePhases.Should().ContainSingle().Which.NodeId.Should().Be("p2");
    }
}

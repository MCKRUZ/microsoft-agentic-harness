using System.Text.Json;
using Application.AI.Common.Interfaces.Memory;
using Application.AI.Common.Interfaces.MetaHarness;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Interfaces;

/// <summary>
/// Tests for record types and interface defaults defined alongside interfaces
/// in <c>Application.AI.Common.Interfaces</c>: <see cref="AgentDecisionEvent"/>,
/// <see cref="DecisionLogQuery"/>, <see cref="EvaluationResult"/>,
/// <see cref="TaskEvaluationResult"/>, and <see cref="ITool"/> default properties.
/// </summary>
public class InterfaceRecordTests
{
    // --- AgentDecisionEvent ---

    [Fact]
    public void AgentDecisionEvent_ConstructsWithRequiredProperties()
    {
        var evt = new AgentDecisionEvent
        {
            Sequence = 1,
            Timestamp = DateTimeOffset.UtcNow,
            EventType = "tool_call",
            ExecutionRunId = "run-123",
            TurnId = "turn-1",
            ToolName = "file_system",
            ResultCategory = "success"
        };

        evt.EventType.Should().Be("tool_call");
        evt.ExecutionRunId.Should().Be("run-123");
        evt.TurnId.Should().Be("turn-1");
        evt.ToolName.Should().Be("file_system");
        evt.ResultCategory.Should().Be("success");
        evt.Sequence.Should().Be(1);
    }

    [Fact]
    public void AgentDecisionEvent_OptionalPropertiesDefaultToNull()
    {
        var evt = new AgentDecisionEvent
        {
            EventType = "decision",
            ExecutionRunId = "run-1",
            TurnId = "turn-1"
        };

        evt.ToolName.Should().BeNull();
        evt.ResultCategory.Should().BeNull();
        evt.Payload.Should().BeNull();
    }

    [Fact]
    public void AgentDecisionEvent_WithPayload_RoundTrips()
    {
        var payload = JsonSerializer.SerializeToElement(new { action = "read", path = "/tmp" });
        var evt = new AgentDecisionEvent
        {
            EventType = "tool_call",
            ExecutionRunId = "run-1",
            TurnId = "turn-1",
            Payload = payload
        };

        evt.Payload.Should().NotBeNull();
        evt.Payload!.Value.GetProperty("action").GetString().Should().Be("read");
    }

    [Fact]
    public void AgentDecisionEvent_RecordEquality_SameValues_AreEqual()
    {
        var ts = DateTimeOffset.UtcNow;
        var a = new AgentDecisionEvent
        {
            Sequence = 5,
            Timestamp = ts,
            EventType = "observation",
            ExecutionRunId = "run-1",
            TurnId = "turn-2"
        };
        var b = new AgentDecisionEvent
        {
            Sequence = 5,
            Timestamp = ts,
            EventType = "observation",
            ExecutionRunId = "run-1",
            TurnId = "turn-2"
        };

        a.Should().Be(b);
    }

    // --- DecisionLogQuery ---

    [Fact]
    public void DecisionLogQuery_ConstructsWithRequiredProperty()
    {
        var query = new DecisionLogQuery
        {
            ExecutionRunId = "run-abc"
        };

        query.ExecutionRunId.Should().Be("run-abc");
        query.TurnId.Should().BeNull();
        query.EventType.Should().BeNull();
        query.ToolName.Should().BeNull();
        query.Since.Should().Be(0);
        query.Limit.Should().Be(100);
    }

    [Fact]
    public void DecisionLogQuery_WithAllFilters_SetsCorrectly()
    {
        var query = new DecisionLogQuery
        {
            ExecutionRunId = "run-1",
            TurnId = "turn-3",
            EventType = "tool_result",
            ToolName = "search",
            Since = 50,
            Limit = 25
        };

        query.TurnId.Should().Be("turn-3");
        query.EventType.Should().Be("tool_result");
        query.ToolName.Should().Be("search");
        query.Since.Should().Be(50);
        query.Limit.Should().Be(25);
    }

    [Fact]
    public void DecisionLogQuery_WithExpression_CreatesModifiedCopy()
    {
        var original = new DecisionLogQuery { ExecutionRunId = "run-1" };
        var modified = original with { Since = 100, Limit = 50 };

        modified.ExecutionRunId.Should().Be("run-1");
        modified.Since.Should().Be(100);
        modified.Limit.Should().Be(50);
        original.Since.Should().Be(0);
    }

    // --- EvaluationResult ---

    [Fact]
    public void EvaluationResult_ConstructsWithAllParameters()
    {
        var candidateId = Guid.NewGuid();
        var perExample = new List<TaskEvaluationResult>
        {
            new("task-1", true, 500),
            new("task-2", false, 300, "Output mismatch")
        };

        var result = new EvaluationResult(candidateId, 0.5, 800, perExample);

        result.CandidateId.Should().Be(candidateId);
        result.PassRate.Should().Be(0.5);
        result.TotalTokenCost.Should().Be(800);
        result.PerExampleResults.Should().HaveCount(2);
    }

    // --- TaskEvaluationResult ---

    [Fact]
    public void TaskEvaluationResult_Passed_HasNullFailureReason()
    {
        var result = new TaskEvaluationResult("task-1", true, 500);

        result.TaskId.Should().Be("task-1");
        result.Passed.Should().BeTrue();
        result.TokenCost.Should().Be(500);
        result.FailureReason.Should().BeNull();
    }

    [Fact]
    public void TaskEvaluationResult_Failed_HasFailureReason()
    {
        var result = new TaskEvaluationResult("task-2", false, 300, "Expected X got Y");

        result.Passed.Should().BeFalse();
        result.FailureReason.Should().Be("Expected X got Y");
    }

    // --- ITool default implementations ---

    [Fact]
    public void ITool_DefaultIsReadOnly_ReturnsFalse()
    {
        var tool = new TestTool();

        // Access through interface to exercise default implementation
        ITool itool = tool;
        itool.IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public void ITool_DefaultIsConcurrencySafe_ReturnsFalse()
    {
        var tool = new TestTool();

        ITool itool = tool;
        itool.IsConcurrencySafe.Should().BeFalse();
    }

    /// <summary>
    /// Minimal ITool implementation that relies on default interface methods for
    /// IsReadOnly and IsConcurrencySafe.
    /// </summary>
    private sealed class TestTool : ITool
    {
        public string Name => "test";
        public string Description => "Test tool";
        public IReadOnlyList<string> SupportedOperations => ["read"];
        public Task<ToolResult> ExecuteAsync(
            string operation,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ToolResult.Ok("ok"));
    }
}

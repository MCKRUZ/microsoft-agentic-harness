using Application.AI.Common.Interfaces.MetaHarness;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Interfaces;

/// <summary>
/// Tests for record types and interface defaults defined alongside interfaces
/// in <c>Application.AI.Common.Interfaces</c>: <see cref="EvaluationResult"/>,
/// <see cref="TaskEvaluationResult"/>, and <see cref="ITool"/> default properties.
/// </summary>
public class InterfaceRecordTests
{
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

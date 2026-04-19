using Domain.Common.Logging;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Logging;

/// <summary>
/// Tests for <see cref="ExecutionScope"/> ToString and ToProperties logic.
/// </summary>
public class ExecutionScopeLogicTests
{
    // ── ToString ──

    [Fact]
    public void ToString_AllPropertiesSet_IncludesAllParts()
    {
        var scope = new ExecutionScope(
            ExecutorId: "agent-1",
            ParentExecutorId: "orchestrator",
            CorrelationId: "corr-123",
            StepNumber: 5,
            OperationName: "file_read");

        var result = scope.ToString();

        result.Should().Contain("Executor=agent-1");
        result.Should().Contain("Parent=orchestrator");
        result.Should().Contain("Corr=corr-123");
        result.Should().Contain("Step=5");
        result.Should().Contain("Op=file_read");
    }

    [Fact]
    public void ToString_OnlyExecutorId_ReturnsJustExecutor()
    {
        var scope = new ExecutionScope(ExecutorId: "agent-1");

        var result = scope.ToString();

        result.Should().Be("Executor=agent-1");
    }

    [Fact]
    public void ToString_NoPropertiesSet_ReturnsEmpty()
    {
        var scope = new ExecutionScope();

        scope.ToString().Should().BeEmpty();
    }

    [Fact]
    public void ToString_PartialProperties_OmitsNulls()
    {
        var scope = new ExecutionScope(
            ExecutorId: "agent-1",
            StepNumber: 3);

        var result = scope.ToString();

        result.Should().Contain("Executor=agent-1");
        result.Should().Contain("Step=3");
        result.Should().NotContain("Parent=");
        result.Should().NotContain("Corr=");
        result.Should().NotContain("Op=");
    }

    // ── ToProperties ──

    [Fact]
    public void ToProperties_AllSet_YieldsAllPairs()
    {
        var scope = new ExecutionScope(
            ExecutorId: "a",
            ParentExecutorId: "b",
            CorrelationId: "c",
            StepNumber: 1,
            OperationName: "d");

        var props = scope.ToProperties().ToList();

        props.Should().HaveCount(5);
        props.Should().Contain(kvp => kvp.Key == "executorId" && (string?)kvp.Value == "a");
        props.Should().Contain(kvp => kvp.Key == "parentExecutorId" && (string?)kvp.Value == "b");
        props.Should().Contain(kvp => kvp.Key == "correlationId" && (string?)kvp.Value == "c");
        props.Should().Contain(kvp => kvp.Key == "stepNumber" && (int?)kvp.Value == 1);
        props.Should().Contain(kvp => kvp.Key == "operationName" && (string?)kvp.Value == "d");
    }

    [Fact]
    public void ToProperties_NoneSet_YieldsNothing()
    {
        var scope = new ExecutionScope();

        scope.ToProperties().Should().BeEmpty();
    }

    [Fact]
    public void ToProperties_PartialSet_YieldsOnlyNonNull()
    {
        var scope = new ExecutionScope(CorrelationId: "corr-42");

        var props = scope.ToProperties().ToList();

        props.Should().ContainSingle();
        props[0].Key.Should().Be("correlationId");
    }
}

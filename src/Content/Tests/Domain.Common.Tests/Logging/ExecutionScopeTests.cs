using Domain.Common.Logging;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Logging;

/// <summary>
/// Tests for <see cref="ExecutionScope"/> record — ToString, ToProperties,
/// record equality, and with-expression immutability.
/// </summary>
public class ExecutionScopeTests
{
    [Fact]
    public void ToString_WithAllProperties_FormatsCorrectly()
    {
        var scope = new ExecutionScope(
            ExecutorId: "agent-1",
            ParentExecutorId: "parent-1",
            CorrelationId: "corr-abc",
            StepNumber: 3,
            OperationName: "tool-call");

        var result = scope.ToString();

        result.Should().Contain("Executor=agent-1");
        result.Should().Contain("Parent=parent-1");
        result.Should().Contain("Corr=corr-abc");
        result.Should().Contain("Step=3");
        result.Should().Contain("Op=tool-call");
    }

    [Fact]
    public void ToString_WithNoProperties_ReturnsEmpty()
    {
        var scope = new ExecutionScope();

        scope.ToString().Should().BeEmpty();
    }

    [Fact]
    public void ToString_WithOnlyExecutorId_ContainsOnlyExecutor()
    {
        var scope = new ExecutionScope(ExecutorId: "agent-1");

        var result = scope.ToString();

        result.Should().Be("Executor=agent-1");
        result.Should().NotContain("Parent=");
    }

    [Fact]
    public void ToProperties_WithAllProperties_YieldsAllKeyValuePairs()
    {
        var scope = new ExecutionScope(
            ExecutorId: "agent-1",
            ParentExecutorId: "parent-1",
            CorrelationId: "corr-abc",
            StepNumber: 3,
            OperationName: "tool-call");

        var props = scope.ToProperties().ToDictionary(kv => kv.Key, kv => kv.Value);

        props.Should().HaveCount(5);
        props["executorId"].Should().Be("agent-1");
        props["parentExecutorId"].Should().Be("parent-1");
        props["correlationId"].Should().Be("corr-abc");
        props["stepNumber"].Should().Be(3);
        props["operationName"].Should().Be("tool-call");
    }

    [Fact]
    public void ToProperties_WithNoProperties_YieldsEmpty()
    {
        var scope = new ExecutionScope();

        scope.ToProperties().Should().BeEmpty();
    }

    [Fact]
    public void ToProperties_WithPartialProperties_YieldsOnlySet()
    {
        var scope = new ExecutionScope(ExecutorId: "x", StepNumber: 1);

        var props = scope.ToProperties().ToList();

        props.Should().HaveCount(2);
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = new ExecutionScope(ExecutorId: "a", StepNumber: 1);
        var b = new ExecutionScope(ExecutorId: "a", StepNumber: 1);

        a.Should().Be(b);
    }

    [Fact]
    public void RecordEquality_DifferentValues_AreNotEqual()
    {
        var a = new ExecutionScope(ExecutorId: "a");
        var b = new ExecutionScope(ExecutorId: "b");

        a.Should().NotBe(b);
    }

    [Fact]
    public void WithExpression_DoesNotMutateOriginal()
    {
        var original = new ExecutionScope(ExecutorId: "a");

        var modified = original with { ExecutorId = "b" };

        original.ExecutorId.Should().Be("a");
        modified.ExecutorId.Should().Be("b");
    }
}

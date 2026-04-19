using Domain.Common.Workflow;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Workflow;

/// <summary>
/// Tests for workflow exceptions: <see cref="DecisionEvaluationException"/>,
/// <see cref="InvalidStateTransitionException"/>, and <see cref="NoMatchingRuleException"/>.
/// </summary>
public class DecisionEvaluationExceptionTests
{
    [Fact]
    public void Constructor_WithMessage_SetsMessage()
    {
        var ex = new DecisionEvaluationException("evaluation failed");

        ex.Message.Should().Be("evaluation failed");
    }

    [Fact]
    public void Constructor_WithMessageAndInner_SetsProperties()
    {
        var inner = new InvalidOperationException("inner");

        var ex = new DecisionEvaluationException("outer", inner);

        ex.Message.Should().Be("outer");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void IsException_BaseType()
    {
        var ex = new DecisionEvaluationException("test");

        ex.Should().BeAssignableTo<Exception>();
    }
}

/// <summary>
/// Tests for <see cref="InvalidStateTransitionException"/> message formatting
/// and property population.
/// </summary>
public class InvalidStateTransitionExceptionTests
{
    [Fact]
    public void Constructor_ThreeArgs_FormatsMessage()
    {
        var ex = new InvalidStateTransitionException("node-1", "not_started", "completed");

        ex.Message.Should().Be(
            "Invalid state transition for node 'node-1': 'not_started' -> 'completed'");
        ex.NodeId.Should().Be("node-1");
        ex.FromStatus.Should().Be("not_started");
        ex.ToStatus.Should().Be("completed");
    }

    [Fact]
    public void Constructor_FourArgs_AppendsCustomMessage()
    {
        var ex = new InvalidStateTransitionException(
            "node-1", "not_started", "completed", "Must go through in_progress first.");

        ex.Message.Should().Contain("Must go through in_progress first.");
        ex.Message.Should().Contain("'not_started' -> 'completed'");
    }

    [Fact]
    public void IsException_BaseType()
    {
        var ex = new InvalidStateTransitionException("n", "a", "b");

        ex.Should().BeAssignableTo<Exception>();
    }
}

/// <summary>
/// Tests for <see cref="NoMatchingRuleException"/>.
/// </summary>
public class NoMatchingRuleExceptionTests
{
    [Fact]
    public void Constructor_WithMessage_SetsMessage()
    {
        var ex = new NoMatchingRuleException("no rule matched");

        ex.Message.Should().Be("no rule matched");
    }

    [Fact]
    public void IsDecisionEvaluationException()
    {
        var ex = new NoMatchingRuleException("test");

        ex.Should().BeAssignableTo<DecisionEvaluationException>();
    }
}

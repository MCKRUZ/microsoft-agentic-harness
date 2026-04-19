using Application.AI.Common.Exceptions;
using Application.Common.Exceptions;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Exceptions;

/// <summary>
/// Tests for <see cref="AgentExecutionException"/> covering all constructors,
/// property assignments, message formatting, and argument validation.
/// </summary>
public class AgentExecutionExceptionTests
{
    [Fact]
    public void DefaultCtor_SetsDefaultMessage()
    {
        var ex = new AgentExecutionException();

        ex.Message.Should().Be("The agent encountered an unrecoverable error during execution.");
        ex.AgentName.Should().BeNull();
        ex.Reason.Should().BeNull();
        ex.InnerException.Should().BeNull();
    }

    [Fact]
    public void MessageCtor_SetsCustomMessage()
    {
        var ex = new AgentExecutionException("custom error");

        ex.Message.Should().Be("custom error");
        ex.AgentName.Should().BeNull();
        ex.Reason.Should().BeNull();
    }

    [Fact]
    public void MessageAndInnerCtor_SetsMessageAndInner()
    {
        var inner = new InvalidOperationException("root cause");
        var ex = new AgentExecutionException("wrapped", inner);

        ex.Message.Should().Be("wrapped");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void StructuredCtor_WithReason_FormatsMessage()
    {
        var ex = new AgentExecutionException("planner", "Exceeded maximum turn limit of 50.");

        ex.Message.Should().Be("Agent 'planner' failed: Exceeded maximum turn limit of 50.");
        ex.AgentName.Should().Be("planner");
        ex.Reason.Should().Be("Exceeded maximum turn limit of 50.");
    }

    [Fact]
    public void StructuredCtor_WithNullReason_FormatsGenericMessage()
    {
        var ex = new AgentExecutionException(agentName: "code-reviewer", reason: null);

        ex.Message.Should().Be("Agent 'code-reviewer' encountered an unrecoverable error.");
        ex.AgentName.Should().Be("code-reviewer");
        ex.Reason.Should().BeNull();
    }

    [Fact]
    public void StructuredCtor_WithInnerException_PreservesInner()
    {
        var inner = new TimeoutException("timed out");
        var ex = new AgentExecutionException("planner", "timeout", inner);

        ex.InnerException.Should().BeSameAs(inner);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StructuredCtor_NullOrWhitespaceAgentName_Throws(string? agentName)
    {
        var act = () => new AgentExecutionException(agentName!, "reason");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsApplicationExceptionBase()
    {
        var ex = new AgentExecutionException();

        ex.Should().BeAssignableTo<ApplicationExceptionBase>();
        ex.Should().BeAssignableTo<Exception>();
    }
}

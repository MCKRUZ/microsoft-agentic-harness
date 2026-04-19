using Application.AI.Common.Exceptions;
using Application.Common.Exceptions;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Exceptions;

/// <summary>
/// Tests for <see cref="ContextBudgetExceededException"/> covering all constructors,
/// property assignments, message formatting, and argument validation.
/// </summary>
public class ContextBudgetExceededExceptionTests
{
    [Fact]
    public void DefaultCtor_SetsDefaultMessage()
    {
        var ex = new ContextBudgetExceededException();

        ex.Message.Should().Be("The agent's context budget has been exceeded.");
        ex.TokenLimit.Should().Be(0);
        ex.TokensUsed.Should().Be(0);
        ex.AgentName.Should().BeNull();
    }

    [Fact]
    public void MessageCtor_SetsCustomMessage()
    {
        var ex = new ContextBudgetExceededException("budget exceeded");

        ex.Message.Should().Be("budget exceeded");
    }

    [Fact]
    public void MessageAndInnerCtor_SetsMessageAndInner()
    {
        var inner = new Exception("cause");
        var ex = new ContextBudgetExceededException("exceeded", inner);

        ex.Message.Should().Be("exceeded");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void StructuredCtor_WithAgentName_FormatsMessage()
    {
        var ex = new ContextBudgetExceededException(128000, 131072, "planner");

        ex.Message.Should().Contain("Agent 'planner'");
        ex.Message.Should().Contain("131,072");
        ex.Message.Should().Contain("128,000");
        ex.TokenLimit.Should().Be(128000);
        ex.TokensUsed.Should().Be(131072);
        ex.AgentName.Should().Be("planner");
    }

    [Fact]
    public void StructuredCtor_WithNullAgentName_FormatsGenericMessage()
    {
        var ex = new ContextBudgetExceededException(200000, 205000);

        ex.Message.Should().StartWith("Context budget exceeded:");
        ex.Message.Should().Contain("205,000");
        ex.Message.Should().Contain("200,000");
        ex.AgentName.Should().BeNull();
    }

    [Fact]
    public void StructuredCtor_NegativeTokenLimit_Throws()
    {
        var act = () => new ContextBudgetExceededException(-1, 100);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void StructuredCtor_NegativeTokensUsed_Throws()
    {
        var act = () => new ContextBudgetExceededException(100, -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void StructuredCtor_ZeroValues_Succeeds()
    {
        var ex = new ContextBudgetExceededException(0, 0);

        ex.TokenLimit.Should().Be(0);
        ex.TokensUsed.Should().Be(0);
    }

    [Fact]
    public void IsApplicationExceptionBase()
    {
        var ex = new ContextBudgetExceededException();
        ex.Should().BeAssignableTo<ApplicationExceptionBase>();
    }
}

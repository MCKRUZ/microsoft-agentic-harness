using Application.AI.Common.Exceptions;
using Application.Common.Exceptions;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Exceptions;

/// <summary>
/// Tests for <see cref="ToolExecutionException"/> covering all constructors,
/// property assignments, message formatting, and argument validation.
/// </summary>
public class ToolExecutionExceptionTests
{
    [Fact]
    public void DefaultCtor_SetsDefaultMessage()
    {
        var ex = new ToolExecutionException();

        ex.Message.Should().Be("A tool execution failed to complete successfully.");
        ex.ToolName.Should().BeNull();
        ex.Reason.Should().BeNull();
    }

    [Fact]
    public void MessageCtor_SetsCustomMessage()
    {
        var ex = new ToolExecutionException("tool failed");

        ex.Message.Should().Be("tool failed");
    }

    [Fact]
    public void MessageAndInnerCtor_SetsMessageAndInner()
    {
        var inner = new TimeoutException("timed out");
        var ex = new ToolExecutionException("failed", inner);

        ex.Message.Should().Be("failed");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void StructuredCtor_WithReason_FormatsMessage()
    {
        var ex = new ToolExecutionException("calculation_engine", "Division by zero in expression.");

        ex.Message.Should().Be("Tool 'calculation_engine' failed: Division by zero in expression.");
        ex.ToolName.Should().Be("calculation_engine");
        ex.Reason.Should().Be("Division by zero in expression.");
    }

    [Fact]
    public void StructuredCtor_WithNullReason_FormatsGenericMessage()
    {
        var ex = new ToolExecutionException(toolName: "file_system", reason: null);

        ex.Message.Should().Be("Tool 'file_system' failed to execute.");
        ex.ToolName.Should().Be("file_system");
        ex.Reason.Should().BeNull();
    }

    [Fact]
    public void StructuredCtor_WithInnerException_PreservesInner()
    {
        var inner = new HttpRequestException("503");
        var ex = new ToolExecutionException("web_fetch", "HTTP 503", inner);

        ex.InnerException.Should().BeSameAs(inner);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StructuredCtor_NullOrWhitespaceToolName_Throws(string? toolName)
    {
        var act = () => new ToolExecutionException(toolName!, "reason");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsApplicationExceptionBase()
    {
        var ex = new ToolExecutionException();
        ex.Should().BeAssignableTo<ApplicationExceptionBase>();
    }
}

using Application.AI.Common.Exceptions;
using Application.Common.Exceptions;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Exceptions;

/// <summary>
/// Tests for <see cref="ContentSafetyException"/> covering all constructors,
/// message formatting with categories, and argument validation.
/// </summary>
public class ContentSafetyExceptionTests
{
    [Fact]
    public void DefaultCtor_SetsDefaultMessage()
    {
        var ex = new ContentSafetyException();

        ex.Message.Should().Be("Content was blocked by safety middleware.");
        ex.Category.Should().BeNull();
    }

    [Fact]
    public void MessageCtor_SetsCustomMessage()
    {
        var ex = new ContentSafetyException("blocked for testing");

        ex.Message.Should().Be("blocked for testing");
        ex.Category.Should().BeNull();
    }

    [Fact]
    public void MessageAndInnerCtor_SetsMessageAndInner()
    {
        var inner = new Exception("cause");
        var ex = new ContentSafetyException("blocked", inner);

        ex.Message.Should().Be("blocked");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void StructuredCtor_WithCategory_FormatsMessage()
    {
        var ex = new ContentSafetyException("Response contained harmful content.", "violence");

        ex.Message.Should().Be("Content blocked [violence]: Response contained harmful content.");
        ex.Category.Should().Be("violence");
    }

    [Fact]
    public void StructuredCtor_WithNullCategory_FormatsWithoutBrackets()
    {
        var ex = new ContentSafetyException(reason: "PII detected.", category: null);

        ex.Message.Should().Be("Content blocked: PII detected.");
        ex.Category.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StructuredCtor_NullOrWhitespaceReason_Throws(string? reason)
    {
        var act = () => new ContentSafetyException(reason!, "violence");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsApplicationExceptionBase()
    {
        var ex = new ContentSafetyException();
        ex.Should().BeAssignableTo<ApplicationExceptionBase>();
    }
}

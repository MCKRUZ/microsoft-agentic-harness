using Application.AI.Common.Exceptions;
using Application.Common.Exceptions;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Exceptions;

/// <summary>
/// Tests for <see cref="SkillParsingException"/> covering both constructors,
/// property assignments, message formatting, and argument validation.
/// </summary>
public class SkillParsingExceptionTests
{
    [Fact]
    public void MessageAndInnerCtor_SetsMessageAndInner()
    {
        var inner = new Exception("yaml error");
        var ex = new SkillParsingException("Failed to parse", inner);

        ex.Message.Should().Be("Failed to parse");
        ex.InnerException.Should().BeSameAs(inner);
        ex.FilePath.Should().BeNull();
    }

    [Fact]
    public void StructuredCtor_FormatsMessage()
    {
        var ex = new SkillParsingException("skills/code-review/SKILL.md", "Missing required 'name' field.");

        ex.Message.Should().Be("Failed to parse skill at 'skills/code-review/SKILL.md': Missing required 'name' field.");
        ex.FilePath.Should().Be("skills/code-review/SKILL.md");
    }

    [Fact]
    public void StructuredCtor_WithInnerException_PreservesInner()
    {
        var inner = new FormatException("bad format");
        var ex = new SkillParsingException("path.md", "invalid", inner);

        ex.InnerException.Should().BeSameAs(inner);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StructuredCtor_NullOrWhitespaceFilePath_Throws(string? filePath)
    {
        var act = () => new SkillParsingException(filePath!, "reason");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StructuredCtor_NullOrWhitespaceReason_Throws(string? reason)
    {
        var act = () => new SkillParsingException("path.md", reason!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsApplicationExceptionBase()
    {
        var ex = new SkillParsingException("msg", new Exception());
        ex.Should().BeAssignableTo<ApplicationExceptionBase>();
    }
}

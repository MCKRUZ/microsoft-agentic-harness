using Application.AI.Common.Exceptions;
using Application.Common.Exceptions;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Exceptions;

/// <summary>
/// Tests for <see cref="SkillNotFoundException"/> covering all constructors,
/// property assignments, message formatting, and argument validation.
/// </summary>
public class SkillNotFoundExceptionTests
{
    [Fact]
    public void DefaultCtor_SetsDefaultMessage()
    {
        var ex = new SkillNotFoundException();

        ex.Message.Should().Be("The requested skill was not found.");
        ex.SkillName.Should().BeNull();
        ex.SkillSource.Should().BeNull();
    }

    [Fact]
    public void MessageCtor_SetsCustomMessage()
    {
        var ex = new SkillNotFoundException("custom not found");

        ex.Message.Should().Be("custom not found");
    }

    [Fact]
    public void MessageAndInnerCtor_SetsMessageAndInner()
    {
        var inner = new Exception("io error");
        var ex = new SkillNotFoundException("not found", inner);

        ex.Message.Should().Be("not found");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void StructuredCtor_WithSource_FormatsMessage()
    {
        var ex = new SkillNotFoundException("code-review", "filesystem");

        ex.Message.Should().Be("Skill 'code-review' was not found in source 'filesystem'.");
        ex.SkillName.Should().Be("code-review");
        ex.SkillSource.Should().Be("filesystem");
    }

    [Fact]
    public void StructuredCtor_WithNullSource_FormatsWithoutSource()
    {
        var ex = new SkillNotFoundException(skillName: "security-scan", source: null);

        ex.Message.Should().Be("Skill 'security-scan' was not found.");
        ex.SkillName.Should().Be("security-scan");
        ex.SkillSource.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StructuredCtor_NullOrWhitespaceSkillName_Throws(string? skillName)
    {
        var act = () => new SkillNotFoundException(skillName!, "filesystem");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsApplicationExceptionBase()
    {
        var ex = new SkillNotFoundException();
        ex.Should().BeAssignableTo<ApplicationExceptionBase>();
    }
}

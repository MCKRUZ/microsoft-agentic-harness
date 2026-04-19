using Application.AI.Common.Exceptions;
using Application.Common.Exceptions;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Exceptions;

/// <summary>
/// Tests for <see cref="AttackDetectionException"/> covering constructors,
/// init-only properties, and the overridden Data dictionary.
/// </summary>
public class AttackDetectionExceptionTests
{
    [Fact]
    public void DefaultCtor_SetsDefaultMessage()
    {
        var ex = new AttackDetectionException();

        ex.Message.Should().Be("An adversarial attack was detected in the input.");
        ex.UserPromptAttackDetected.Should().BeFalse();
        ex.DocumentsWithAttacksCount.Should().Be(0);
        ex.DetectedCategories.Should().BeEmpty();
    }

    [Fact]
    public void MessageCtor_SetsCustomMessage()
    {
        var ex = new AttackDetectionException("injection detected");

        ex.Message.Should().Be("injection detected");
    }

    [Fact]
    public void MessageAndInnerCtor_SetsMessageAndInner()
    {
        var inner = new Exception("root");
        var ex = new AttackDetectionException("attack", inner);

        ex.Message.Should().Be("attack");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void InitProperties_CanBeSet()
    {
        var categories = new List<string> { "UserPrompt", "DocumentAttack" };

        var ex = new AttackDetectionException("test")
        {
            UserPromptAttackDetected = true,
            DocumentsWithAttacksCount = 3,
            DetectedCategories = categories
        };

        ex.UserPromptAttackDetected.Should().BeTrue();
        ex.DocumentsWithAttacksCount.Should().Be(3);
        ex.DetectedCategories.Should().BeEquivalentTo(categories);
    }

    [Fact]
    public void Data_ReturnsStructuredDictionary()
    {
        var ex = new AttackDetectionException("test")
        {
            UserPromptAttackDetected = true,
            DocumentsWithAttacksCount = 2,
            DetectedCategories = ["UserPrompt"]
        };

        var data = ex.Data;

        data["userPromptAttackDetected"].Should().Be(true);
        data["documentsWithAttacksCount"].Should().Be(2);
        data["detectedCategories"].Should().BeEquivalentTo(new[] { "UserPrompt" });
    }

    [Fact]
    public void Data_ReturnsSameInstanceOnMultipleAccesses()
    {
        var ex = new AttackDetectionException();

        var first = ex.Data;
        var second = ex.Data;

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void IsApplicationExceptionBase()
    {
        var ex = new AttackDetectionException();
        ex.Should().BeAssignableTo<ApplicationExceptionBase>();
    }
}

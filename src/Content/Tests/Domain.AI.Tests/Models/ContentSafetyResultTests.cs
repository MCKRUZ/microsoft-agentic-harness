using Domain.AI.Models;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Models;

/// <summary>
/// Tests for <see cref="ContentSafetyResult"/> and <see cref="ContentScreeningTarget"/>.
/// </summary>
public sealed class ContentSafetyResultTests
{
    [Fact]
    public void Constructor_Blocked_SetsAllValues()
    {
        var result = new ContentSafetyResult(true, "Contains PII", "pii");

        result.IsBlocked.Should().BeTrue();
        result.BlockReason.Should().Be("Contains PII");
        result.Category.Should().Be("pii");
    }

    [Fact]
    public void Constructor_NotBlocked_SetsNulls()
    {
        var result = new ContentSafetyResult(false, null, null);

        result.IsBlocked.Should().BeFalse();
        result.BlockReason.Should().BeNull();
        result.Category.Should().BeNull();
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var r1 = new ContentSafetyResult(true, "reason", "hate");
        var r2 = new ContentSafetyResult(true, "reason", "hate");

        r1.Should().Be(r2);
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var original = new ContentSafetyResult(true, "blocked", "violence");
        var updated = original with { IsBlocked = false };

        updated.IsBlocked.Should().BeFalse();
        original.IsBlocked.Should().BeTrue();
    }

    [Theory]
    [InlineData(ContentScreeningTarget.Input, 0)]
    [InlineData(ContentScreeningTarget.Output, 1)]
    [InlineData(ContentScreeningTarget.Both, 2)]
    public void ContentScreeningTarget_Values_HaveExpectedIntegers(ContentScreeningTarget value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void ContentScreeningTarget_HasExactlyThreeValues()
    {
        Enum.GetValues<ContentScreeningTarget>().Should().HaveCount(3);
    }
}

using Domain.AI.Models;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Models;

/// <summary>
/// Tests for <see cref="ToolResult"/> record — factory methods, properties, edge cases.
/// </summary>
public sealed class ToolResultTests
{
    [Fact]
    public void Ok_SetsSuccessTrue_WithOutput()
    {
        var result = ToolResult.Ok("File content here");

        result.Success.Should().BeTrue();
        result.Output.Should().Be("File content here");
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Fail_SetsSuccessFalse_WithError()
    {
        var result = ToolResult.Fail("File not found");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("File not found");
        result.Output.Should().BeNull();
    }

    [Fact]
    public void Ok_EmptyOutput_IsValid()
    {
        var result = ToolResult.Ok("");

        result.Success.Should().BeTrue();
        result.Output.Should().BeEmpty();
    }

    [Fact]
    public void Fail_EmptyError_IsValid()
    {
        var result = ToolResult.Fail("");

        result.Success.Should().BeFalse();
        result.Error.Should().BeEmpty();
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var r1 = ToolResult.Ok("output");
        var r2 = ToolResult.Ok("output");

        r1.Should().Be(r2);
    }

    [Fact]
    public void Equality_DifferentOutput_AreNotEqual()
    {
        var r1 = ToolResult.Ok("output-a");
        var r2 = ToolResult.Ok("output-b");

        r1.Should().NotBe(r2);
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var original = ToolResult.Ok("original");
        var updated = original with { Output = "updated" };

        updated.Output.Should().Be("updated");
        original.Output.Should().Be("original");
    }
}

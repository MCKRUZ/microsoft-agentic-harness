using Domain.AI.Hooks;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Hooks;

/// <summary>
/// Tests for <see cref="HookResult"/> record — factory methods, defaults, modification scenarios.
/// </summary>
public sealed class HookResultTests
{
    [Fact]
    public void PassThrough_AllDefaultsCorrect()
    {
        var result = HookResult.PassThrough();

        result.Continue.Should().BeTrue();
        result.SuppressOutput.Should().BeFalse();
        result.ModifiedInput.Should().BeNull();
        result.ModifiedOutput.Should().BeNull();
        result.AdditionalContext.Should().BeNull();
        result.StopReason.Should().BeNull();
    }

    [Fact]
    public void Block_SetsStopReasonAndContinueFalse()
    {
        var result = HookResult.Block("Unsafe operation");

        result.Continue.Should().BeFalse();
        result.StopReason.Should().Be("Unsafe operation");
    }

    [Fact]
    public void WithExpression_CanAddModifiedInput()
    {
        var modified = new Dictionary<string, object?> { ["path"] = "/safe/path" };
        var result = HookResult.PassThrough() with { ModifiedInput = modified };

        result.Continue.Should().BeTrue();
        result.ModifiedInput.Should().ContainKey("path");
    }

    [Fact]
    public void WithExpression_CanAddModifiedOutput()
    {
        var result = HookResult.PassThrough() with { ModifiedOutput = "Sanitized output" };

        result.ModifiedOutput.Should().Be("Sanitized output");
    }

    [Fact]
    public void WithExpression_CanAddAdditionalContext()
    {
        var result = HookResult.PassThrough() with { AdditionalContext = "Extra context" };

        result.AdditionalContext.Should().Be("Extra context");
    }

    [Fact]
    public void WithExpression_CanSuppressOutput()
    {
        var result = HookResult.PassThrough() with { SuppressOutput = true };

        result.SuppressOutput.Should().BeTrue();
    }
}

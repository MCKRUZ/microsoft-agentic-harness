using Application.AI.Common.Services.Governance;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>Tests for <see cref="ToolCallOncePolicy"/>: registration, lookup, and the safe defaults.</summary>
public sealed class ToolCallOncePolicyTests
{
    [Fact]
    public void IsCallOnce_UnregisteredTool_ReturnsFalse()
    {
        var policy = new ToolCallOncePolicy();

        policy.IsCallOnce("never_registered").Should().BeFalse();
    }

    [Fact]
    public void IsCallOnce_RegisteredTool_ReturnsTrue()
    {
        var policy = new ToolCallOncePolicy();

        policy.Register("start_diagnostic_session");

        policy.IsCallOnce("start_diagnostic_session").Should().BeTrue();
    }

    [Fact]
    public void IsCallOnce_IsCaseInsensitive()
    {
        var policy = new ToolCallOncePolicy();

        policy.Register("Start_Diagnostic_Session");

        policy.IsCallOnce("start_diagnostic_session").Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_BlankName_IsANoOp(string? toolName)
    {
        var policy = new ToolCallOncePolicy();

        policy.Register(toolName!);

        policy.IsCallOnce(toolName!).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsCallOnce_BlankName_ReturnsFalse(string? toolName)
    {
        var policy = new ToolCallOncePolicy();

        policy.IsCallOnce(toolName!).Should().BeFalse();
    }
}

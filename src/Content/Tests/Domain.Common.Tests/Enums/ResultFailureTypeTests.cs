using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Enums;

/// <summary>
/// Tests for <see cref="ResultFailureType"/> enum values, parsing, and ToString.
/// </summary>
public class ResultFailureTypeTests
{
    [Theory]
    [InlineData(ResultFailureType.None, 0)]
    [InlineData(ResultFailureType.General, 1)]
    [InlineData(ResultFailureType.Validation, 2)]
    [InlineData(ResultFailureType.Unauthorized, 3)]
    [InlineData(ResultFailureType.Forbidden, 4)]
    [InlineData(ResultFailureType.ContentBlocked, 5)]
    [InlineData(ResultFailureType.NotFound, 6)]
    [InlineData(ResultFailureType.PermissionRequired, 7)]
    public void Value_HasExpectedInteger(ResultFailureType type, int expected)
    {
        ((int)type).Should().Be(expected);
    }

    [Fact]
    public void AllValues_AreDistinct()
    {
        var values = Enum.GetValues<ResultFailureType>();

        values.Should().OnlyHaveUniqueItems();
        values.Should().HaveCount(8);
    }

    [Theory]
    [InlineData("None", ResultFailureType.None)]
    [InlineData("General", ResultFailureType.General)]
    [InlineData("Validation", ResultFailureType.Validation)]
    [InlineData("NotFound", ResultFailureType.NotFound)]
    public void Parse_FromString_ReturnsCorrectValue(string input, ResultFailureType expected)
    {
        Enum.Parse<ResultFailureType>(input).Should().Be(expected);
    }

    [Fact]
    public void ToString_ReturnsName()
    {
        ResultFailureType.ContentBlocked.ToString().Should().Be("ContentBlocked");
    }
}

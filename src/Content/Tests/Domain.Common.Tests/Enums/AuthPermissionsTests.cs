using Domain.Common.Enums;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Enums;

/// <summary>
/// Tests for <see cref="AuthPermissions"/> enum values and parsing.
/// </summary>
public class AuthPermissionsTests
{
    [Theory]
    [InlineData(AuthPermissions.Access, 0)]
    [InlineData(AuthPermissions.TermsAgreement, 1)]
    [InlineData(AuthPermissions.Admin, 2)]
    public void Value_HasExpectedInteger(AuthPermissions permission, int expected)
    {
        ((int)permission).Should().Be(expected);
    }

    [Fact]
    public void AllValues_AreDistinct()
    {
        var values = Enum.GetValues<AuthPermissions>();

        values.Should().OnlyHaveUniqueItems();
        values.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("Access", AuthPermissions.Access)]
    [InlineData("Admin", AuthPermissions.Admin)]
    public void Parse_FromString_ReturnsCorrectValue(string input, AuthPermissions expected)
    {
        Enum.Parse<AuthPermissions>(input).Should().Be(expected);
    }

    [Fact]
    public void ToString_ReturnsName()
    {
        AuthPermissions.TermsAgreement.ToString().Should().Be("TermsAgreement");
    }
}

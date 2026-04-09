using System.ComponentModel;
using Domain.Common.Extensions;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests;

public class EnumExtensionsTests
{
    private enum TestEnum
    {
        [Description("First value description")]
        WithDescription,

        NoDescription,

        [Description("")]
        EmptyDescription
    }

    [Fact]
    public void ToDescriptionString_WithAttribute_ReturnsDescription()
    {
        TestEnum.WithDescription.ToDescriptionString().Should().Be("First value description");
    }

    [Fact]
    public void ToDescriptionString_WithoutAttribute_ReturnsEmpty()
    {
        TestEnum.NoDescription.ToDescriptionString().Should().BeEmpty();
    }

    [Fact]
    public void ToDescriptionString_EmptyDescription_ReturnsEmpty()
    {
        TestEnum.EmptyDescription.ToDescriptionString().Should().BeEmpty();
    }
}

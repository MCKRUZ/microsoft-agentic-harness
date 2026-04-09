using Application.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Application.Common.Tests.Helpers;

public class EmbeddedResourceHelperTests
{
    [Fact]
    public void ReadAsString_MissingResource_ThrowsInvalidOperationException()
    {
        var act = () => EmbeddedResourceHelper.ReadAsString(
            "NonExistent.Resource", "Application.Common");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public void ReadAsString_NullResourceName_ThrowsArgumentException()
    {
        var act = () => EmbeddedResourceHelper.ReadAsString(null!, "Application.Common");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReadAsString_NullAssemblyName_ThrowsArgumentException()
    {
        var act = () => EmbeddedResourceHelper.ReadAsString("Some.Resource", null!);

        act.Should().Throw<ArgumentException>();
    }
}

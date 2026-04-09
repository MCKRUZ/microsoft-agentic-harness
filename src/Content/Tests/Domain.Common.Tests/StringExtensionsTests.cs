using Domain.Common.Extensions;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests;

public class StringExtensionsTests
{
    [Fact]
    public void Truncate_ShortString_ReturnsOriginal()
    {
        "hello".Truncate(10).Should().Be("hello");
    }

    [Fact]
    public void Truncate_LongString_TruncatesWithEllipsis()
    {
        "hello world".Truncate(5).Should().Be("hello...");
    }

    [Fact]
    public void Truncate_NullOrEmpty_ReturnsAsIs()
    {
        ((string)null!).Truncate(5).Should().BeNull();
        "".Truncate(5).Should().BeEmpty();
    }

    [Fact]
    public void Truncate_ExactLength_ReturnsOriginal()
    {
        "hello".Truncate(5).Should().Be("hello");
    }
}

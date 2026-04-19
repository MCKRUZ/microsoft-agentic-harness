using Domain.AI.Models;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Models;

/// <summary>
/// Tests for <see cref="FileSearchResult"/> record — construction, defaults, equality.
/// </summary>
public sealed class FileSearchResultTests
{
    [Fact]
    public void Constructor_WithRequiredProperties_SetsValues()
    {
        var result = new FileSearchResult
        {
            FilePath = "/src/Program.cs",
            Snippet = "static void Main()"
        };

        result.FilePath.Should().Be("/src/Program.cs");
        result.Snippet.Should().Be("static void Main()");
    }

    [Fact]
    public void Defaults_LineNumber_IsNull()
    {
        var result = new FileSearchResult
        {
            FilePath = "/test",
            Snippet = "test"
        };

        result.LineNumber.Should().BeNull();
    }

    [Fact]
    public void LineNumber_WhenSet_RetainsValue()
    {
        var result = new FileSearchResult
        {
            FilePath = "/src/Service.cs",
            Snippet = "return result;",
            LineNumber = 42
        };

        result.LineNumber.Should().Be(42);
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var r1 = new FileSearchResult { FilePath = "/a", Snippet = "s", LineNumber = 1 };
        var r2 = new FileSearchResult { FilePath = "/a", Snippet = "s", LineNumber = 1 };

        r1.Should().Be(r2);
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var original = new FileSearchResult { FilePath = "/a", Snippet = "s" };
        var updated = original with { LineNumber = 10 };

        updated.LineNumber.Should().Be(10);
        original.LineNumber.Should().BeNull();
    }
}

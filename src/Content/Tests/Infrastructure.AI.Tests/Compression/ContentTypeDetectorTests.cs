using Domain.AI.Compression.Enums;
using FluentAssertions;
using Infrastructure.AI.Compression;
using Xunit;

namespace Infrastructure.AI.Tests.Compression;

public sealed class ContentTypeDetectorTests
{
    [Fact]
    public void Detect_ValidJsonObject_ReturnsJson()
    {
        var input = """{"name": "test", "value": 42}""";
        ContentTypeDetector.Detect(input).Should().Be(ToolOutputCategory.Json);
    }

    [Fact]
    public void Detect_ValidJsonArray_ReturnsJson()
    {
        var input = """[{"id": 1}, {"id": 2}]""";
        ContentTypeDetector.Detect(input).Should().Be(ToolOutputCategory.Json);
    }

    [Fact]
    public void Detect_FilePathsWithLineNumbers_ReturnsFileContent()
    {
        var input = """
            src/Program.cs:10: public static void Main()
            src/Program.cs:11: {
            src/Program.cs:12:     Console.WriteLine("Hello");
            src/Program.cs:13: }
            """;
        ContentTypeDetector.Detect(input).Should().Be(ToolOutputCategory.FileContent);
    }

    [Fact]
    public void Detect_TabDelimitedRows_ReturnsTabular()
    {
        var input = "Name\tAge\tCity\nAlice\t30\tSeattle\nBob\t25\tPortland\nCharlie\t35\tDenver\n";
        ContentTypeDetector.Detect(input).Should().Be(ToolOutputCategory.Tabular);
    }

    [Fact]
    public void Detect_RepeatedStructuredLines_ReturnsSearchResults()
    {
        var lines = string.Join('\n', Enumerable.Range(1, 20)
            .Select(i => $"Result {i}: Found match in file{i}.cs at line {i * 10}"));
        ContentTypeDetector.Detect(lines).Should().Be(ToolOutputCategory.SearchResults);
    }

    [Fact]
    public void Detect_PlainProse_ReturnsFreeText()
    {
        var input = "The quick brown fox jumps over the lazy dog. This is a paragraph of unstructured text that doesn't match any pattern.";
        ContentTypeDetector.Detect(input).Should().Be(ToolOutputCategory.FreeText);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Detect_EmptyOrNull_ReturnsFreeText(string? input)
    {
        ContentTypeDetector.Detect(input!).Should().Be(ToolOutputCategory.FreeText);
    }
}

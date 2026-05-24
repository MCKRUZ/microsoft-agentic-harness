using Domain.AI.Compression.Enums;
using FluentAssertions;
using Infrastructure.AI.Compression.Strategies;
using Xunit;

namespace Infrastructure.AI.Tests.Compression.Strategies;

public sealed class StructuredTextCompressionStrategyTests
{
    private readonly StructuredTextCompressionStrategy _sut = new();

    [Fact]
    public void CanHandle_FileContent_ReturnsTrue()
    {
        _sut.CanHandle(ToolOutputCategory.FileContent).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_SearchResults_ReturnsTrue()
    {
        _sut.CanHandle(ToolOutputCategory.SearchResults).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_Tabular_ReturnsTrue()
    {
        _sut.CanHandle(ToolOutputCategory.Tabular).Should().BeTrue();
    }

    [Fact]
    public async Task CompressAsync_DuplicateLines_DeduplicatesWithCount()
    {
        var lines = string.Join('\n', Enumerable.Repeat("ERROR: Connection refused", 500));

        var result = await _sut.CompressAsync(lines, 50);

        result.WasCompressed.Should().BeTrue();
        result.Output.Should().Contain("similar lines omitted");
        result.CompressedTokens.Should().BeLessThan(result.OriginalTokens);
    }

    [Fact]
    public async Task CompressAsync_LongFile_PreservesHeadAndTail()
    {
        var lines = Enumerable.Range(1, 400).Select(i => $"Line {i}: content here").ToArray();
        var input = string.Join('\n', lines);

        var result = await _sut.CompressAsync(input, 100);

        result.WasCompressed.Should().BeTrue();
        result.Output.Should().Contain("Line 1:");
        result.Output.Should().Contain("Line 400:");
        result.Output.Should().Contain("lines omitted");
    }

    [Fact]
    public async Task CompressAsync_ShortOutput_ReturnsPassthrough()
    {
        var input = "Line 1\nLine 2\nLine 3";

        var result = await _sut.CompressAsync(input, 100);

        result.WasCompressed.Should().BeFalse();
        result.Output.Should().Be(input);
    }
}

using Domain.AI.Compression.Enums;
using FluentAssertions;
using Infrastructure.AI.Compression.Strategies;
using Xunit;

namespace Infrastructure.AI.Tests.Compression.Strategies;

public sealed class JsonCompressionStrategyTests
{
    private readonly JsonCompressionStrategy _sut = new();

    [Fact]
    public void CanHandle_Json_ReturnsTrue()
    {
        _sut.CanHandle(ToolOutputCategory.Json).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_FreeText_ReturnsFalse()
    {
        _sut.CanHandle(ToolOutputCategory.FreeText).Should().BeFalse();
    }

    [Fact]
    public async Task CompressAsync_LargeArray_TruncatesWithCount()
    {
        var items = Enumerable.Range(1, 20).Select(i => new { id = i, name = $"item{i}" });
        var json = System.Text.Json.JsonSerializer.Serialize(items);

        var result = await _sut.CompressAsync(json, 50);

        result.WasCompressed.Should().BeTrue();
        result.Output.Should().Contain("items omitted");
        result.CompressedTokens.Should().BeLessThan(result.OriginalTokens);
    }

    [Fact]
    public async Task CompressAsync_DeeplyNested_PrunesAtDepth4()
    {
        var json = """{"a":{"b":{"c":{"d":{"e":{"f":"deep"}}}}}}""";

        var result = await _sut.CompressAsync(json, 20);

        result.WasCompressed.Should().BeTrue();
        result.Output.Should().Contain("nested object");
    }

    [Fact]
    public async Task CompressAsync_SmallJson_ReturnsPassthrough()
    {
        var json = """{"name":"test"}""";

        var result = await _sut.CompressAsync(json, 100);

        result.WasCompressed.Should().BeFalse();
        result.Output.Should().Be(json);
    }

    [Fact]
    public async Task CompressAsync_InvalidJson_ReturnsFalseWasCompressed()
    {
        var result = await _sut.CompressAsync("not json {{{", 50);

        result.WasCompressed.Should().BeFalse();
    }

    [Fact]
    public async Task CompressAsync_EmptyString_ReturnsPassthrough()
    {
        var result = await _sut.CompressAsync("", 100);

        result.WasCompressed.Should().BeFalse();
    }

    [Fact]
    public async Task CompressAsync_LowSignalKeys_RemovedWhenOverBudget()
    {
        var json = """{"data":"important","_links":{"self":"http://example.com"},"metadata":{"created":"2024-01-01"},"pagination":{"page":1,"total":100}}""";

        var result = await _sut.CompressAsync(json, 15);

        result.WasCompressed.Should().BeTrue();
        result.Output.Should().Contain("important");
        result.Output.Should().NotContain("_links");
    }
}

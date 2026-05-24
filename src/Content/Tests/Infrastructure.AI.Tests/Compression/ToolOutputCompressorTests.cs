using Application.AI.Common.Interfaces.Compression;
using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;
using FluentAssertions;
using Infrastructure.AI.Compression;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Compression;

public sealed class ToolOutputCompressorTests
{
    private readonly Mock<ICompressionStrategy> _mockJsonStrategy = new();
    private readonly Mock<ICompressionStrategy> _mockFreeTextStrategy = new();

    public ToolOutputCompressorTests()
    {
        _mockJsonStrategy.Setup(s => s.CanHandle(ToolOutputCategory.Json)).Returns(true);
        _mockFreeTextStrategy.Setup(s => s.CanHandle(ToolOutputCategory.FreeText)).Returns(true);
    }

    private ToolOutputCompressor CreateSut() => new(
        [_mockJsonStrategy.Object, _mockFreeTextStrategy.Object],
        Mock.Of<ILogger<ToolOutputCompressor>>());

    [Fact]
    public async Task CompressAsync_JsonCategory_RoutesToJsonStrategy()
    {
        var bigJson = "{" + string.Join(",", Enumerable.Range(0, 100).Select(i => $"\"key{i}\":\"value{i}\"")) + "}";
        _mockJsonStrategy
            .Setup(s => s.CompressAsync(It.IsAny<string>(), 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompressionResult
            {
                Output = "compressed", OriginalTokens = 500, CompressedTokens = 50,
                Strategy = "Json", WasCompressed = true
            });

        var result = await CreateSut().CompressAsync(bigJson, ToolOutputCategory.Json, 100);

        result.Strategy.Should().Be("Json");
        _mockJsonStrategy.Verify(s => s.CompressAsync(bigJson, 100, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompressAsync_StrategyFails_FallsBackToFreeText()
    {
        var largeData = new string('d', 500);
        _mockJsonStrategy
            .Setup(s => s.CompressAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompressionResult
            {
                Output = largeData, OriginalTokens = 500, CompressedTokens = 500,
                Strategy = "Json", WasCompressed = false
            });

        _mockFreeTextStrategy
            .Setup(s => s.CompressAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompressionResult
            {
                Output = "fallback", OriginalTokens = 500, CompressedTokens = 50,
                Strategy = "FreeText", WasCompressed = true
            });

        var result = await CreateSut().CompressAsync(largeData, ToolOutputCategory.Json, 100);

        result.Strategy.Should().Be("FreeText");
    }

    [Fact]
    public async Task CompressAsync_StrategyThrows_FallsBackToHardTruncate()
    {
        _mockJsonStrategy
            .Setup(s => s.CompressAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("strategy error"));

        _mockFreeTextStrategy
            .Setup(s => s.CompressAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("also fails"));

        var result = await CreateSut().CompressAsync(new string('x', 1000), ToolOutputCategory.Json, 50);

        result.WasCompressed.Should().BeTrue();
        result.Strategy.Should().Be("HardTruncate");
    }

    [Fact]
    public async Task CompressAsync_UnknownCategory_FallsBackToFreeText()
    {
        var largeData = new string('d', 500);
        _mockFreeTextStrategy
            .Setup(s => s.CompressAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompressionResult
            {
                Output = "compressed", OriginalTokens = 500, CompressedTokens = 50,
                Strategy = "FreeText", WasCompressed = true
            });

        var result = await CreateSut().CompressAsync(largeData, (ToolOutputCategory)99, 100);

        result.Strategy.Should().Be("FreeText");
    }

    [Fact]
    public async Task CompressAsync_BelowThreshold_ReturnsPassthrough()
    {
        var result = await CreateSut().CompressAsync("short", ToolOutputCategory.Json, 100);

        result.WasCompressed.Should().BeFalse();
        result.Output.Should().Be("short");
        _mockJsonStrategy.Verify(
            s => s.CompressAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CompressAsync_EmptyOutput_ReturnsPassthrough()
    {
        var result = await CreateSut().CompressAsync("", ToolOutputCategory.Json, 100);

        result.WasCompressed.Should().BeFalse();
    }
}

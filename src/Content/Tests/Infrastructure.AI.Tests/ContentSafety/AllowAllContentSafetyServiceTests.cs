using Domain.AI.Models;
using FluentAssertions;
using Infrastructure.AI.ContentSafety;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.ContentSafety;

/// <summary>
/// Tests for <see cref="AllowAllContentSafetyService"/> verifying
/// pass-through behavior and logging.
/// </summary>
public sealed class AllowAllContentSafetyServiceTests
{
    private readonly Mock<ILogger<AllowAllContentSafetyService>> _logger = new();
    private readonly AllowAllContentSafetyService _sut;

    public AllowAllContentSafetyServiceTests()
    {
        _sut = new AllowAllContentSafetyService(_logger.Object);
    }

    [Fact]
    public async Task ScreenAsync_AlwaysAllows()
    {
        var result = await _sut.ScreenAsync("Any content at all", CancellationToken.None);

        result.IsBlocked.Should().BeFalse();
    }

    [Fact]
    public async Task ScreenAsync_ReturnsNullBlockReason()
    {
        var result = await _sut.ScreenAsync("content", CancellationToken.None);

        result.BlockReason.Should().BeNull();
    }

    [Fact]
    public async Task ScreenAsync_ReturnsNullCategory()
    {
        var result = await _sut.ScreenAsync("content", CancellationToken.None);

        result.Category.Should().BeNull();
    }

    [Fact]
    public async Task ScreenAsync_ReturnsCompletedValueTask()
    {
        var task = _sut.ScreenAsync("content", CancellationToken.None);

        task.IsCompleted.Should().BeTrue();
        await task;
    }

    [Fact]
    public async Task ScreenAsync_EmptyContent_StillAllows()
    {
        var result = await _sut.ScreenAsync("", CancellationToken.None);

        result.IsBlocked.Should().BeFalse();
    }

    [Fact]
    public async Task ScreenAsync_LongContent_StillAllows()
    {
        var longContent = new string('x', 100_000);

        var result = await _sut.ScreenAsync(longContent, CancellationToken.None);

        result.IsBlocked.Should().BeFalse();
    }
}

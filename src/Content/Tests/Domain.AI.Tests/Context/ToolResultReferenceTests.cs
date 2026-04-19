using Domain.AI.Context;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Context;

/// <summary>
/// Tests for <see cref="ToolResultReference"/> record — construction, computed properties, equality.
/// </summary>
public sealed class ToolResultReferenceTests
{
    [Fact]
    public void Constructor_WithRequiredProperties_SetsValues()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var reference = new ToolResultReference
        {
            ResultId = "result-001",
            ToolName = "file_system",
            PreviewContent = "First 200 chars...",
            SizeChars = 50000,
            Timestamp = timestamp
        };

        reference.ResultId.Should().Be("result-001");
        reference.ToolName.Should().Be("file_system");
        reference.PreviewContent.Should().Be("First 200 chars...");
        reference.SizeChars.Should().Be(50000);
        reference.Timestamp.Should().Be(timestamp);
    }

    [Fact]
    public void Defaults_OptionalProperties_AreNull()
    {
        var reference = CreateReference();

        reference.Operation.Should().BeNull();
        reference.FullContentPath.Should().BeNull();
    }

    [Fact]
    public void EstimatedTokens_ComputesDivisionByFour()
    {
        var reference = CreateReference(sizeChars: 400);

        reference.EstimatedTokens.Should().Be(100);
    }

    [Fact]
    public void EstimatedTokens_OddSize_RoundsDown()
    {
        var reference = CreateReference(sizeChars: 401);

        reference.EstimatedTokens.Should().Be(100);
    }

    [Fact]
    public void EstimatedTokens_ZeroSize_ReturnsZero()
    {
        var reference = CreateReference(sizeChars: 0);

        reference.EstimatedTokens.Should().Be(0);
    }

    [Fact]
    public void IsPersistedToDisk_NoPath_ReturnsFalse()
    {
        var reference = CreateReference();

        reference.IsPersistedToDisk.Should().BeFalse();
    }

    [Fact]
    public void IsPersistedToDisk_WithPath_ReturnsTrue()
    {
        var reference = CreateReference() with { FullContentPath = "/tmp/results/result-001.json" };

        reference.IsPersistedToDisk.Should().BeTrue();
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var original = CreateReference(sizeChars: 1000);
        var updated = original with { SizeChars = 2000 };

        updated.SizeChars.Should().Be(2000);
        updated.EstimatedTokens.Should().Be(500);
        original.SizeChars.Should().Be(1000);
    }

    private static ToolResultReference CreateReference(int sizeChars = 100) =>
        new()
        {
            ResultId = "r-1",
            ToolName = "test_tool",
            PreviewContent = "preview",
            SizeChars = sizeChars,
            Timestamp = DateTimeOffset.UtcNow
        };
}

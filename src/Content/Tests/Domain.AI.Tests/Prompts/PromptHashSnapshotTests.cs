using Domain.AI.Prompts;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Prompts;

/// <summary>
/// Tests for <see cref="PromptHashSnapshot"/> record — construction, equality.
/// </summary>
public sealed class PromptHashSnapshotTests
{
    [Fact]
    public void Constructor_WithRequiredProperties_SetsValues()
    {
        var perTool = new Dictionary<string, string>
        {
            ["bash"] = "abc123",
            ["file_system"] = "def456"
        };
        var timestamp = DateTimeOffset.UtcNow;

        var snapshot = new PromptHashSnapshot
        {
            SystemHash = "sys-hash",
            ToolsHash = "tools-hash",
            PerToolHashes = perTool,
            Timestamp = timestamp
        };

        snapshot.SystemHash.Should().Be("sys-hash");
        snapshot.ToolsHash.Should().Be("tools-hash");
        snapshot.PerToolHashes.Should().HaveCount(2);
        snapshot.Timestamp.Should().Be(timestamp);
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var perTool = new Dictionary<string, string> { ["bash"] = "hash1" };
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var s1 = new PromptHashSnapshot { SystemHash = "s", ToolsHash = "t", PerToolHashes = perTool, Timestamp = ts };
        var s2 = new PromptHashSnapshot { SystemHash = "s", ToolsHash = "t", PerToolHashes = perTool, Timestamp = ts };

        s1.Should().Be(s2);
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var original = new PromptHashSnapshot
        {
            SystemHash = "old",
            ToolsHash = "tools",
            PerToolHashes = new Dictionary<string, string>(),
            Timestamp = DateTimeOffset.UtcNow
        };

        var updated = original with { SystemHash = "new" };

        updated.SystemHash.Should().Be("new");
        original.SystemHash.Should().Be("old");
    }
}

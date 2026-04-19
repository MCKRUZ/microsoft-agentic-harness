using Domain.AI.Skills;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Skills;

/// <summary>
/// Tests for <see cref="SkillCacheStatistics"/> record — construction, equality.
/// </summary>
public sealed class SkillCacheStatisticsTests
{
    [Fact]
    public void Constructor_SetsAllValues()
    {
        var clearTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var stats = new SkillCacheStatistics(10, 50, 5, clearTime);

        stats.LoadedSkillCount.Should().Be(10);
        stats.CacheHits.Should().Be(50);
        stats.CacheMisses.Should().Be(5);
        stats.LastClearTime.Should().Be(clearTime);
    }

    [Fact]
    public void Constructor_NullClearTime_IsValid()
    {
        var stats = new SkillCacheStatistics(0, 0, 0, null);

        stats.LastClearTime.Should().BeNull();
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var time = DateTime.UtcNow;
        var s1 = new SkillCacheStatistics(5, 10, 2, time);
        var s2 = new SkillCacheStatistics(5, 10, 2, time);

        s1.Should().Be(s2);
    }

    [Fact]
    public void Equality_DifferentValues_AreNotEqual()
    {
        var s1 = new SkillCacheStatistics(5, 10, 2, null);
        var s2 = new SkillCacheStatistics(5, 11, 2, null);

        s1.Should().NotBe(s2);
    }
}

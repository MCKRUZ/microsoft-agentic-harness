using Domain.AI.Skills;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Skills;

/// <summary>
/// Tests for <see cref="SkillChangedEventArgs"/> — construction, EventArgs inheritance.
/// </summary>
public sealed class SkillChangedEventArgsTests
{
    [Fact]
    public void Constructor_SetsAllRequiredProperties()
    {
        var args = new SkillChangedEventArgs
        {
            SkillId = "research-skill",
            ChangeType = WatcherChangeTypes.Changed,
            FilePath = "/skills/research/SKILL.md"
        };

        args.SkillId.Should().Be("research-skill");
        args.ChangeType.Should().Be(WatcherChangeTypes.Changed);
        args.FilePath.Should().Be("/skills/research/SKILL.md");
    }

    [Fact]
    public void InheritsFrom_EventArgs()
    {
        var args = new SkillChangedEventArgs
        {
            SkillId = "test",
            ChangeType = WatcherChangeTypes.Created,
            FilePath = "/test"
        };

        args.Should().BeAssignableTo<EventArgs>();
    }

    [Theory]
    [InlineData(WatcherChangeTypes.Created)]
    [InlineData(WatcherChangeTypes.Changed)]
    [InlineData(WatcherChangeTypes.Deleted)]
    public void ChangeType_SupportsAllRelevantValues(WatcherChangeTypes changeType)
    {
        var args = new SkillChangedEventArgs
        {
            SkillId = "skill",
            ChangeType = changeType,
            FilePath = "/path"
        };

        args.ChangeType.Should().Be(changeType);
    }
}

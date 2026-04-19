using Domain.AI.Permissions;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Permissions;

/// <summary>
/// Tests for permission-related enums: <see cref="PermissionBehaviorType"/>,
/// <see cref="PermissionRuleSource"/>.
/// </summary>
public sealed class PermissionEnumTests
{
    [Theory]
    [InlineData(PermissionBehaviorType.Allow, 0)]
    [InlineData(PermissionBehaviorType.Deny, 1)]
    [InlineData(PermissionBehaviorType.Ask, 2)]
    public void PermissionBehaviorType_Values_HaveExpectedIntegers(PermissionBehaviorType value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void PermissionBehaviorType_HasExactlyThreeValues()
    {
        Enum.GetValues<PermissionBehaviorType>().Should().HaveCount(3);
    }

    [Theory]
    [InlineData(PermissionRuleSource.AgentManifest, 0)]
    [InlineData(PermissionRuleSource.SkillDefinition, 1)]
    [InlineData(PermissionRuleSource.UserSettings, 2)]
    [InlineData(PermissionRuleSource.ProjectSettings, 3)]
    [InlineData(PermissionRuleSource.LocalSettings, 4)]
    [InlineData(PermissionRuleSource.SessionOverride, 5)]
    [InlineData(PermissionRuleSource.PolicySettings, 6)]
    [InlineData(PermissionRuleSource.CliArgument, 7)]
    public void PermissionRuleSource_Values_HaveExpectedIntegers(PermissionRuleSource value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void PermissionRuleSource_HasExactlyEightValues()
    {
        Enum.GetValues<PermissionRuleSource>().Should().HaveCount(8);
    }
}

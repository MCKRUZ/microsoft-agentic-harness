using Domain.AI.Permissions;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Permissions;

/// <summary>
/// Tests for <see cref="ToolPermissionRule"/> record — construction, defaults, equality.
/// </summary>
public sealed class ToolPermissionRuleTests
{
    [Fact]
    public void Constructor_AllParameters_SetsValues()
    {
        var rule = new ToolPermissionRule(
            "file_system",
            "write:*",
            PermissionBehaviorType.Ask,
            PermissionRuleSource.ProjectSettings,
            10,
            true);

        rule.ToolPattern.Should().Be("file_system");
        rule.OperationPattern.Should().Be("write:*");
        rule.Behavior.Should().Be(PermissionBehaviorType.Ask);
        rule.Source.Should().Be(PermissionRuleSource.ProjectSettings);
        rule.Priority.Should().Be(10);
        rule.IsBypassImmune.Should().BeTrue();
    }

    [Fact]
    public void Default_IsBypassImmune_IsFalse()
    {
        var rule = new ToolPermissionRule(
            "bash", null, PermissionBehaviorType.Allow,
            PermissionRuleSource.UserSettings, 5);

        rule.IsBypassImmune.Should().BeFalse();
    }

    [Fact]
    public void Default_OperationPattern_IsNull()
    {
        var rule = new ToolPermissionRule(
            "*", null, PermissionBehaviorType.Deny,
            PermissionRuleSource.PolicySettings, 0);

        rule.OperationPattern.Should().BeNull();
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var r1 = new ToolPermissionRule("bash", "exec", PermissionBehaviorType.Allow, PermissionRuleSource.CliArgument, 1);
        var r2 = new ToolPermissionRule("bash", "exec", PermissionBehaviorType.Allow, PermissionRuleSource.CliArgument, 1);

        r1.Should().Be(r2);
    }

    [Fact]
    public void Equality_DifferentPriority_AreNotEqual()
    {
        var r1 = new ToolPermissionRule("bash", null, PermissionBehaviorType.Allow, PermissionRuleSource.CliArgument, 1);
        var r2 = new ToolPermissionRule("bash", null, PermissionBehaviorType.Allow, PermissionRuleSource.CliArgument, 2);

        r1.Should().NotBe(r2);
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var original = new ToolPermissionRule("bash", null, PermissionBehaviorType.Ask, PermissionRuleSource.UserSettings, 5);
        var updated = original with { Behavior = PermissionBehaviorType.Allow };

        updated.Behavior.Should().Be(PermissionBehaviorType.Allow);
        original.Behavior.Should().Be(PermissionBehaviorType.Ask);
    }
}

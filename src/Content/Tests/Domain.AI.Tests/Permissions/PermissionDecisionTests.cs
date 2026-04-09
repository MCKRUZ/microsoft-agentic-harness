using Domain.AI.Permissions;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Permissions;

public sealed class PermissionDecisionTests
{
    [Fact]
    public void AllowFactory_CreatesCorrectDecision()
    {
        var rule = new ToolPermissionRule("bash", null, PermissionBehaviorType.Allow, PermissionRuleSource.ProjectSettings, 1);

        var decision = PermissionDecision.Allow("Allowed by project settings.", rule);

        decision.Behavior.Should().Be(PermissionBehaviorType.Allow);
        decision.Reason.Should().Be("Allowed by project settings.");
        decision.MatchedRule.Should().BeSameAs(rule);
        decision.Source.Should().Be(PermissionRuleSource.ProjectSettings);
    }

    [Fact]
    public void DenyFactory_CreatesCorrectDecision()
    {
        var rule = new ToolPermissionRule("*", null, PermissionBehaviorType.Deny, PermissionRuleSource.PolicySettings, 0);

        var decision = PermissionDecision.Deny("Denied by policy.", rule);

        decision.Behavior.Should().Be(PermissionBehaviorType.Deny);
        decision.Reason.Should().Be("Denied by policy.");
        decision.MatchedRule.Should().BeSameAs(rule);
        decision.Source.Should().Be(PermissionRuleSource.PolicySettings);
    }

    [Fact]
    public void AskFactory_CreatesCorrectDecision()
    {
        var decision = PermissionDecision.Ask("No rule matched, defaulting to Ask.");

        decision.Behavior.Should().Be(PermissionBehaviorType.Ask);
        decision.Reason.Should().Be("No rule matched, defaulting to Ask.");
        decision.MatchedRule.Should().BeNull();
        decision.Source.Should().BeNull();
    }

    [Fact]
    public void AllowFactory_WithoutRule_SetsSourceNull()
    {
        var decision = PermissionDecision.Allow("Auto-approved.");

        decision.Source.Should().BeNull();
        decision.MatchedRule.Should().BeNull();
    }
}

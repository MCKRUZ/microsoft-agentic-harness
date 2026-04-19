using Domain.AI.Agents;
using Domain.AI.Permissions;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Agents;

/// <summary>
/// Tests for <see cref="SubagentDefinition"/> record — defaults, construction, equality.
/// </summary>
public sealed class SubagentDefinitionTests
{
    [Fact]
    public void Defaults_OptionalProperties_AreCorrect()
    {
        var def = new SubagentDefinition { AgentType = SubagentType.General };

        def.ToolAllowlist.Should().BeNull();
        def.ToolDenylist.Should().BeNull();
        def.PermissionMode.Should().Be(PermissionBehaviorType.Ask);
        def.MaxTurns.Should().Be(10);
        def.ModelOverride.Should().BeNull();
        def.SystemPromptOverride.Should().BeNull();
        def.InheritParentTools.Should().BeTrue();
    }

    [Fact]
    public void Constructor_WithAllProperties_SetsValues()
    {
        var allowlist = new List<string> { "file_system", "bash" };
        var denylist = new List<string> { "dangerous_tool" };

        var def = new SubagentDefinition
        {
            AgentType = SubagentType.Execute,
            ToolAllowlist = allowlist,
            ToolDenylist = denylist,
            PermissionMode = PermissionBehaviorType.Allow,
            MaxTurns = 25,
            ModelOverride = "gpt-4o-mini",
            SystemPromptOverride = "Be concise",
            InheritParentTools = false
        };

        def.AgentType.Should().Be(SubagentType.Execute);
        def.ToolAllowlist.Should().BeEquivalentTo(allowlist);
        def.ToolDenylist.Should().BeEquivalentTo(denylist);
        def.PermissionMode.Should().Be(PermissionBehaviorType.Allow);
        def.MaxTurns.Should().Be(25);
        def.ModelOverride.Should().Be("gpt-4o-mini");
        def.SystemPromptOverride.Should().Be("Be concise");
        def.InheritParentTools.Should().BeFalse();
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var original = new SubagentDefinition
        {
            AgentType = SubagentType.Explore,
            MaxTurns = 5
        };

        var updated = original with { MaxTurns = 20 };

        updated.MaxTurns.Should().Be(20);
        original.MaxTurns.Should().Be(5);
    }
}

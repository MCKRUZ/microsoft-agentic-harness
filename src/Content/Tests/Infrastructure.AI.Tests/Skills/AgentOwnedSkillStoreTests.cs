using Domain.AI.Skills;
using FluentAssertions;
using Infrastructure.AI.Skills;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Unit tests for <see cref="AgentOwnedSkillStore"/>. These pin the isolation guarantees that make
/// agent-owned skills safe: a skill is resolvable only for its owning agent, invisible to others, and
/// re-registration is idempotent. Matching is case-insensitive to mirror the global registry.
/// </summary>
public sealed class AgentOwnedSkillStoreTests
{
    private static SkillDefinition Skill(string id) => new() { Id = id, Name = id };

    [Fact]
    public void TryGet_RegisteredSkill_ReturnsItForOwningAgent()
    {
        var store = new AgentOwnedSkillStore();
        var skill = Skill("nested");
        store.Register("agent-a", skill);

        store.TryGet("agent-a", "nested").Should().BeSameAs(skill);
    }

    [Fact]
    public void TryGet_SkillOwnedByAnotherAgent_IsInvisible()
    {
        var store = new AgentOwnedSkillStore();
        store.Register("agent-a", Skill("nested"));

        // Agent B never registered "nested": one agent's owned skill must not leak to another.
        store.TryGet("agent-b", "nested").Should().BeNull();
    }

    [Fact]
    public void TryGet_UnknownSkillForKnownAgent_ReturnsNull()
    {
        var store = new AgentOwnedSkillStore();
        store.Register("agent-a", Skill("nested"));

        store.TryGet("agent-a", "other").Should().BeNull();
    }

    [Fact]
    public void TryGet_AgentIdAndSkillId_AreCaseInsensitive()
    {
        var store = new AgentOwnedSkillStore();
        var skill = Skill("Nested");
        store.Register("Agent-A", skill);

        store.TryGet("agent-a", "nested").Should().BeSameAs(skill);
    }

    [Fact]
    public void Register_SameAgentAndSkillId_OverwritesIdempotently()
    {
        var store = new AgentOwnedSkillStore();
        store.Register("agent-a", Skill("nested"));
        var replacement = Skill("nested");
        store.Register("agent-a", replacement);

        store.TryGet("agent-a", "nested").Should().BeSameAs(replacement);
        store.GetForAgent("agent-a").Should().ContainSingle();
    }

    [Fact]
    public void GetForAgent_ReturnsAllSkillsForThatAgentOnly()
    {
        var store = new AgentOwnedSkillStore();
        store.Register("agent-a", Skill("s1"));
        store.Register("agent-a", Skill("s2"));
        store.Register("agent-b", Skill("s3"));

        store.GetForAgent("agent-a").Select(s => s.Id).Should().BeEquivalentTo(["s1", "s2"]);
    }

    [Fact]
    public void GetForAgent_UnknownAgent_ReturnsEmpty()
    {
        var store = new AgentOwnedSkillStore();

        store.GetForAgent("nobody").Should().BeEmpty();
    }

    [Fact]
    public void TryGet_NullOrWhitespaceKeys_ReturnNull()
    {
        var store = new AgentOwnedSkillStore();
        store.Register("agent-a", Skill("nested"));

        store.TryGet("", "nested").Should().BeNull();
        store.TryGet("agent-a", " ").Should().BeNull();
    }

    [Fact]
    public void Register_BlankAgentId_Throws()
    {
        var store = new AgentOwnedSkillStore();

        var act = () => store.Register("  ", Skill("nested"));

        act.Should().Throw<ArgumentException>();
    }
}

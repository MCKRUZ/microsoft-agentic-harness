using Domain.AI.Prompts;
using FluentAssertions;
using Infrastructure.AI.Prompts;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts;

public class InMemoryPromptSectionCacheTests
{
    private readonly InMemoryPromptSectionCache _cache = new();

    private static SystemPromptSection CreateSection(
        SystemPromptSectionType type = SystemPromptSectionType.AgentIdentity,
        string content = "test content") =>
        new("Test", type, 10, true, 5, content);

    [Fact]
    public void TryGet_Miss_ReturnsFalse()
    {
        var result = _cache.TryGet("agent-1", SystemPromptSectionType.AgentIdentity, out var section);

        result.Should().BeFalse();
        section.Should().BeNull();
    }

    [Fact]
    public void Set_ThenTryGet_ReturnsSection()
    {
        var expected = CreateSection();
        _cache.Set("agent-1", expected);

        var result = _cache.TryGet("agent-1", SystemPromptSectionType.AgentIdentity, out var section);

        result.Should().BeTrue();
        section.Should().Be(expected);
    }

    [Fact]
    public void Set_DifferentAgents_IsolatesSections()
    {
        var section1 = CreateSection(content: "agent 1 content");
        var section2 = CreateSection(content: "agent 2 content");

        _cache.Set("agent-1", section1);
        _cache.Set("agent-2", section2);

        _cache.TryGet("agent-1", SystemPromptSectionType.AgentIdentity, out var retrieved1);
        _cache.TryGet("agent-2", SystemPromptSectionType.AgentIdentity, out var retrieved2);

        retrieved1!.Content.Should().Be("agent 1 content");
        retrieved2!.Content.Should().Be("agent 2 content");
    }

    [Fact]
    public void Invalidate_RemovesMatchingType()
    {
        _cache.Set("agent-1", CreateSection(SystemPromptSectionType.AgentIdentity));
        _cache.Set("agent-1", CreateSection(SystemPromptSectionType.ToolSchemas, "tools"));
        _cache.Set("agent-2", CreateSection(SystemPromptSectionType.AgentIdentity, "agent2"));

        _cache.Invalidate(SystemPromptSectionType.AgentIdentity);

        _cache.TryGet("agent-1", SystemPromptSectionType.AgentIdentity, out _).Should().BeFalse();
        _cache.TryGet("agent-2", SystemPromptSectionType.AgentIdentity, out _).Should().BeFalse();
        _cache.TryGet("agent-1", SystemPromptSectionType.ToolSchemas, out _).Should().BeTrue();
    }

    [Fact]
    public void InvalidateAll_ClearsAll()
    {
        _cache.Set("agent-1", CreateSection(SystemPromptSectionType.AgentIdentity));
        _cache.Set("agent-1", CreateSection(SystemPromptSectionType.ToolSchemas, "tools"));
        _cache.Set("agent-2", CreateSection(SystemPromptSectionType.SessionState, "state"));

        _cache.InvalidateAll();

        _cache.TryGet("agent-1", SystemPromptSectionType.AgentIdentity, out _).Should().BeFalse();
        _cache.TryGet("agent-1", SystemPromptSectionType.ToolSchemas, out _).Should().BeFalse();
        _cache.TryGet("agent-2", SystemPromptSectionType.SessionState, out _).Should().BeFalse();
    }

    [Fact]
    public void Set_Upserts_ExistingEntry()
    {
        var original = CreateSection(content: "original");
        var updated = CreateSection(content: "updated");

        _cache.Set("agent-1", original);
        _cache.Set("agent-1", updated);

        _cache.TryGet("agent-1", SystemPromptSectionType.AgentIdentity, out var section);
        section!.Content.Should().Be("updated");
    }
}

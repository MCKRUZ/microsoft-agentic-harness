using Domain.AI.Prompts;
using FluentAssertions;
using Infrastructure.AI.Prompts.Sections;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts.Sections;

/// <summary>
/// Tests for <see cref="AgentIdentitySectionProvider"/>. The cacheable identity
/// content must be a pure function of the <c>agentId</c> parameter — the cache
/// key — never of ambient scoped state, so a cached section can never carry one
/// conversation's identity into another (cross-agent cache poisoning).
/// </summary>
public class AgentIdentitySectionProviderTests
{
    private readonly AgentIdentitySectionProvider _provider = new();

    [Fact]
    public async Task GetSectionAsync_ReturnsAgentIdentity()
    {
        var section = await _provider.GetSectionAsync("ResearchAgent");

        section.Should().NotBeNull();
        section!.Content.Should().Contain("ResearchAgent");
        section.Type.Should().Be(SystemPromptSectionType.AgentIdentity);
        section.Priority.Should().Be(10);
    }

    [Fact]
    public async Task GetSectionAsync_IsCacheable()
    {
        var section = await _provider.GetSectionAsync("TestAgent");

        section.Should().NotBeNull();
        section!.IsCacheable.Should().BeTrue();
    }

    [Fact]
    public async Task GetSectionAsync_ContentDerivesOnlyFromCacheKeyInput()
    {
        // Regression: the cache keys on the agentId PARAMETER. Content derived
        // from anything else (e.g. the scoped IAgentExecutionContext) would let
        // a scope bound to agent-b poison the cached section under agent-a's key.
        var section = await _provider.GetSectionAsync("agent-a");

        section.Should().NotBeNull();
        section!.Content.Should().Be("You are agent-a.");
    }

    [Fact]
    public async Task GetSectionAsync_ReturnsNull_WhenAgentIdEmpty()
    {
        var section = await _provider.GetSectionAsync("");

        section.Should().BeNull();
    }

    [Fact]
    public void SectionType_IsAgentIdentity()
    {
        _provider.SectionType.Should().Be(SystemPromptSectionType.AgentIdentity);
    }

    [Fact]
    public async Task GetSectionAsync_EstimatedTokens_IsPositive()
    {
        var section = await _provider.GetSectionAsync("Agent");

        section.Should().NotBeNull();
        section!.EstimatedTokens.Should().BeGreaterThan(0);
    }
}

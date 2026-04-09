using Application.AI.Common.Interfaces.Agent;
using Domain.AI.Prompts;
using FluentAssertions;
using Infrastructure.AI.Prompts.Sections;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts.Sections;

public class AgentIdentitySectionProviderTests
{
    private readonly Mock<IAgentExecutionContext> _contextMock = new();

    [Fact]
    public async Task GetSectionAsync_ReturnsAgentIdentity()
    {
        _contextMock.Setup(c => c.AgentId).Returns("ResearchAgent");

        var provider = new AgentIdentitySectionProvider(_contextMock.Object);

        var section = await provider.GetSectionAsync("ResearchAgent");

        section.Should().NotBeNull();
        section!.Content.Should().Contain("ResearchAgent");
        section.Type.Should().Be(SystemPromptSectionType.AgentIdentity);
        section.Priority.Should().Be(10);
    }

    [Fact]
    public async Task GetSectionAsync_IsCacheable()
    {
        _contextMock.Setup(c => c.AgentId).Returns("TestAgent");

        var provider = new AgentIdentitySectionProvider(_contextMock.Object);

        var section = await provider.GetSectionAsync("TestAgent");

        section.Should().NotBeNull();
        section!.IsCacheable.Should().BeTrue();
    }

    [Fact]
    public async Task GetSectionAsync_FallsBackToAgentIdParameter_WhenContextNull()
    {
        _contextMock.Setup(c => c.AgentId).Returns((string?)null);

        var provider = new AgentIdentitySectionProvider(_contextMock.Object);

        var section = await provider.GetSectionAsync("fallback-agent");

        section.Should().NotBeNull();
        section!.Content.Should().Contain("fallback-agent");
    }

    [Fact]
    public async Task GetSectionAsync_ReturnsNull_WhenBothIdsEmpty()
    {
        _contextMock.Setup(c => c.AgentId).Returns((string?)null);

        var provider = new AgentIdentitySectionProvider(_contextMock.Object);

        var section = await provider.GetSectionAsync("");

        section.Should().BeNull();
    }

    [Fact]
    public void SectionType_IsAgentIdentity()
    {
        var provider = new AgentIdentitySectionProvider(_contextMock.Object);

        provider.SectionType.Should().Be(SystemPromptSectionType.AgentIdentity);
    }

    [Fact]
    public async Task GetSectionAsync_EstimatedTokens_IsPositive()
    {
        _contextMock.Setup(c => c.AgentId).Returns("Agent");

        var provider = new AgentIdentitySectionProvider(_contextMock.Object);

        var section = await provider.GetSectionAsync("Agent");

        section.Should().NotBeNull();
        section!.EstimatedTokens.Should().BeGreaterThan(0);
    }
}

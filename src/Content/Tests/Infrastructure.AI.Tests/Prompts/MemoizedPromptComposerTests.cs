using Application.AI.Common.Interfaces.Prompts;
using Domain.AI.Prompts;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts;

public class MemoizedPromptComposerTests
{
    private readonly Mock<IPromptSectionCache> _cacheMock = new();
    private readonly ILogger<Infrastructure.AI.Prompts.MemoizedPromptComposer> _logger =
        NullLogger<Infrastructure.AI.Prompts.MemoizedPromptComposer>.Instance;

    private Infrastructure.AI.Prompts.MemoizedPromptComposer CreateComposer(
        params IPromptSectionProvider[] providers) =>
        new(providers, _cacheMock.Object, _logger);

    private static SystemPromptSection CreateSection(
        string name,
        SystemPromptSectionType type,
        int priority,
        bool cacheable,
        int tokens,
        string content) =>
        new(name, type, priority, cacheable, tokens, content);

    private static Mock<IPromptSectionProvider> CreateProviderMock(
        SystemPromptSectionType type,
        SystemPromptSection? section)
    {
        var mock = new Mock<IPromptSectionProvider>();
        mock.Setup(p => p.SectionType).Returns(type);
        mock.Setup(p => p.GetSectionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(section);
        return mock;
    }

    [Fact]
    public async Task ComposeAsync_CacheHit_DoesNotRecompute()
    {
        var cached = CreateSection("Identity", SystemPromptSectionType.AgentIdentity, 10, true, 5, "cached identity");

        _cacheMock.Setup(c => c.TryGet("agent-1", SystemPromptSectionType.AgentIdentity, out cached))
            .Returns(true);

        var providerMock = CreateProviderMock(SystemPromptSectionType.AgentIdentity, cached);
        var composer = CreateComposer(providerMock.Object);

        var result = await composer.ComposeAsync("agent-1", 1000);

        result.Should().Be("cached identity");
        providerMock.Verify(
            p => p.GetSectionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ComposeAsync_CacheMiss_ComputesAndCaches()
    {
        var section = CreateSection("Identity", SystemPromptSectionType.AgentIdentity, 10, true, 5, "computed");

        SystemPromptSection? nullSection = null;
        _cacheMock.Setup(c => c.TryGet("agent-1", SystemPromptSectionType.AgentIdentity, out nullSection))
            .Returns(false);

        var providerMock = CreateProviderMock(SystemPromptSectionType.AgentIdentity, section);
        var composer = CreateComposer(providerMock.Object);

        var result = await composer.ComposeAsync("agent-1", 1000);

        result.Should().Be("computed");
        _cacheMock.Verify(c => c.Set("agent-1", section), Times.Once);
    }

    [Fact]
    public async Task ComposeAsync_NonCacheableSection_NotCached()
    {
        var section = CreateSection("State", SystemPromptSectionType.SessionState, 50, false, 5, "state");

        SystemPromptSection? nullSection = null;
        _cacheMock.Setup(c => c.TryGet("agent-1", SystemPromptSectionType.SessionState, out nullSection))
            .Returns(false);

        var providerMock = CreateProviderMock(SystemPromptSectionType.SessionState, section);
        var composer = CreateComposer(providerMock.Object);

        await composer.ComposeAsync("agent-1", 1000);

        _cacheMock.Verify(c => c.Set(It.IsAny<string>(), It.IsAny<SystemPromptSection>()), Times.Never);
    }

    [Fact]
    public async Task ComposeAsync_BudgetExceeded_DropsLowPrioritySections()
    {
        var high = CreateSection("High", SystemPromptSectionType.AgentIdentity, 10, false, 100, "high priority");
        var low = CreateSection("Low", SystemPromptSectionType.SessionState, 50, false, 200, "low priority");

        SystemPromptSection? nullSection = null;
        _cacheMock.Setup(c => c.TryGet(It.IsAny<string>(), It.IsAny<SystemPromptSectionType>(), out nullSection))
            .Returns(false);

        var provider1 = CreateProviderMock(SystemPromptSectionType.AgentIdentity, high);
        var provider2 = CreateProviderMock(SystemPromptSectionType.SessionState, low);
        var composer = CreateComposer(provider1.Object, provider2.Object);

        var result = await composer.ComposeAsync("agent-1", 150);

        result.Should().Contain("high priority");
        result.Should().NotContain("low priority");
    }

    [Fact]
    public async Task ComposeAsync_SortsBySectionPriority()
    {
        var low = CreateSection("Low", SystemPromptSectionType.SessionState, 50, false, 10, "BBB");
        var high = CreateSection("High", SystemPromptSectionType.AgentIdentity, 10, false, 10, "AAA");

        SystemPromptSection? nullSection = null;
        _cacheMock.Setup(c => c.TryGet(It.IsAny<string>(), It.IsAny<SystemPromptSectionType>(), out nullSection))
            .Returns(false);

        // Register low-priority first to verify sorting
        var provider1 = CreateProviderMock(SystemPromptSectionType.SessionState, low);
        var provider2 = CreateProviderMock(SystemPromptSectionType.AgentIdentity, high);
        var composer = CreateComposer(provider1.Object, provider2.Object);

        var result = await composer.ComposeAsync("agent-1", 10000);

        result.Should().Be("AAA\n\nBBB");
    }

    [Fact]
    public async Task ComposeAsync_SkipsNullSections()
    {
        var section = CreateSection("Identity", SystemPromptSectionType.AgentIdentity, 10, false, 10, "identity");

        SystemPromptSection? nullSection = null;
        _cacheMock.Setup(c => c.TryGet(It.IsAny<string>(), It.IsAny<SystemPromptSectionType>(), out nullSection))
            .Returns(false);

        var provider1 = CreateProviderMock(SystemPromptSectionType.AgentIdentity, section);
        var provider2 = CreateProviderMock(SystemPromptSectionType.SkillInstructions, null);
        var composer = CreateComposer(provider1.Object, provider2.Object);

        var result = await composer.ComposeAsync("agent-1", 10000);

        result.Should().Be("identity");
    }

    [Fact]
    public async Task ComposeAsync_SectionTypesFilter_ExcludesNonAuthoritativeSections()
    {
        var identity = CreateSection("Identity", SystemPromptSectionType.AgentIdentity, 10, false, 5, "You are agent-1.");
        var skills = CreateSection("Skills", SystemPromptSectionType.SkillInstructions, 20, false, 5, "SKILL BODY");
        var tools = CreateSection("Tools", SystemPromptSectionType.ToolSchemas, 30, false, 5, "TOOL SCHEMAS");
        var state = CreateSection("State", SystemPromptSectionType.SessionState, 50, false, 5, "SESSION STATE");

        SystemPromptSection? nullSection = null;
        _cacheMock.Setup(c => c.TryGet(It.IsAny<string>(), It.IsAny<SystemPromptSectionType>(), out nullSection))
            .Returns(false);

        var composer = CreateComposer(
            CreateProviderMock(SystemPromptSectionType.AgentIdentity, identity).Object,
            CreateProviderMock(SystemPromptSectionType.SkillInstructions, skills).Object,
            CreateProviderMock(SystemPromptSectionType.ToolSchemas, tools).Object,
            CreateProviderMock(SystemPromptSectionType.SessionState, state).Object);

        var authoritative = new HashSet<SystemPromptSectionType>
        {
            SystemPromptSectionType.AgentIdentity,
            SystemPromptSectionType.SkillInstructions,
            SystemPromptSectionType.PermissionRules,
        };

        var result = await composer.ComposeAsync("agent-1", 10_000, authoritative);

        result.Should().Contain("You are agent-1.");
        result.Should().Contain("SKILL BODY");
        result.Should().NotContain("TOOL SCHEMAS", "tool schemas are excluded from the authoritative static prompt");
        result.Should().NotContain("SESSION STATE", "session state is excluded from the authoritative static prompt");
    }

    [Fact]
    public async Task ComposeAsync_TightBudget_DropsPermissionRulesBeforeSkillInstructions()
    {
        // Authoritative set at production priorities: identity(10) < skill instructions(20) < permission rules(40).
        var identity = CreateSection("Identity", SystemPromptSectionType.AgentIdentity, 10, false, 50, "IDENTITY");
        var skills = CreateSection("Skills", SystemPromptSectionType.SkillInstructions, 20, false, 50, "SKILL BODY");
        var permissions = CreateSection("Permissions", SystemPromptSectionType.PermissionRules, 40, false, 50, "PERMISSION RULES");

        SystemPromptSection? nullSection = null;
        _cacheMock.Setup(c => c.TryGet(It.IsAny<string>(), It.IsAny<SystemPromptSectionType>(), out nullSection))
            .Returns(false);

        var composer = CreateComposer(
            CreateProviderMock(SystemPromptSectionType.AgentIdentity, identity).Object,
            CreateProviderMock(SystemPromptSectionType.SkillInstructions, skills).Object,
            CreateProviderMock(SystemPromptSectionType.PermissionRules, permissions).Object);

        var authoritative = new HashSet<SystemPromptSectionType>
        {
            SystemPromptSectionType.AgentIdentity,
            SystemPromptSectionType.SkillInstructions,
            SystemPromptSectionType.PermissionRules,
        };

        // Budget fits identity(50) + skill instructions(50) = 100, but not the tail permission rules(150 > 120).
        var result = await composer.ComposeAsync("agent-1", 120, authoritative);

        result.Should().Contain("IDENTITY");
        result.Should().Contain("SKILL BODY", "skill instructions must never be dropped before the lower-priority permission rules");
        result.Should().NotContain("PERMISSION RULES", "the lowest-priority tail section is dropped first when over budget");
    }

    [Fact]
    public void InvalidateSection_ClearsSpecificType()
    {
        var composer = CreateComposer();

        composer.InvalidateSection(SystemPromptSectionType.ToolSchemas);

        _cacheMock.Verify(c => c.Invalidate(SystemPromptSectionType.ToolSchemas), Times.Once);
    }

    [Fact]
    public void InvalidateAll_ClearsEverything()
    {
        var composer = CreateComposer();

        composer.InvalidateAll();

        _cacheMock.Verify(c => c.InvalidateAll(), Times.Once);
    }

    [Fact]
    public async Task ComposeAsync_ZeroTokenEstimate_UsesHeuristic()
    {
        // Section with EstimatedTokens=0 should get auto-estimated
        var section = CreateSection("Identity", SystemPromptSectionType.AgentIdentity, 10, false, 0, "Hello world test");

        SystemPromptSection? nullSection = null;
        _cacheMock.Setup(c => c.TryGet(It.IsAny<string>(), It.IsAny<SystemPromptSectionType>(), out nullSection))
            .Returns(false);

        var providerMock = CreateProviderMock(SystemPromptSectionType.AgentIdentity, section);
        var composer = CreateComposer(providerMock.Object);

        // Budget of 1 token should drop the section since "Hello world test" is ~4 tokens
        var result = await composer.ComposeAsync("agent-1", 1);

        result.Should().BeEmpty();
    }
}

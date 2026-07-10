using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Tests.Fakes;
using Domain.AI.Agents;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Tests that <see cref="AgentFactory"/> resolves an agent's own nested skills (from
/// <see cref="IAgentOwnedSkillStore"/>) ahead of the global registry, keyed by the owning agent id,
/// and falls back to the global registry when there is no owner or no owned skill.
/// </summary>
public sealed class AgentFactoryAgentOwnedSkillTests
{
    /// <summary>Minimal in-memory <see cref="IAgentOwnedSkillStore"/> so this Application-layer test
    /// need not depend on the Infrastructure implementation.</summary>
    private sealed class FakeOwnedSkillStore : IAgentOwnedSkillStore
    {
        private readonly Dictionary<(string, string), SkillDefinition> _skills = new();

        public void Register(string agentId, SkillDefinition skill) =>
            _skills[(agentId, skill.Id)] = skill;

        public SkillDefinition? TryGet(string agentId, string skillId) =>
            _skills.TryGetValue((agentId, skillId), out var s) ? s : null;

        public IReadOnlyList<SkillDefinition> GetForAgent(string agentId) =>
            _skills.Where(kv => kv.Key.Item1 == agentId).Select(kv => kv.Value).ToList();
    }

    private readonly Mock<ISkillMetadataRegistry> _skillRegistry = new();
    private readonly Mock<AgentExecutionContextFactory> _contextFactory;
    private readonly FakeOwnedSkillStore _ownedStore = new();
    private readonly AgentFactory _factory;
    private IReadOnlyList<SkillDefinition>? _capturedSkills;

    public AgentFactoryAgentOwnedSkillTests()
    {
        var chatClientFactory = new Mock<IChatClientFactory>();
        chatClientFactory.Setup(f => f.IsAvailable(It.IsAny<AIAgentFrameworkClientType>())).Returns(true);
        chatClientFactory
            .Setup(f => f.GetChatClientAsync(It.IsAny<AIAgentFrameworkClientType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeChatClient());

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    DefaultDeployment = "gpt-4o",
                    ClientType = AIAgentFrameworkClientType.AzureOpenAI
                }
            }
        };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);

        _contextFactory = new Mock<AgentExecutionContextFactory>(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance,
            null!, null!, null!, null!, null!, null!);

        _contextFactory
            .Setup(f => f.MapToAgentContextAsync(
                It.IsAny<IReadOnlyList<SkillDefinition>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<IReadOnlyList<string>?>()))
            .Callback<IReadOnlyList<SkillDefinition>, SkillAgentOptions, IReadOnlyList<string>?>(
                (skills, _, _) => _capturedSkills = skills)
            .ReturnsAsync(new AgentExecutionContext
            {
                Name = "TestAgent",
                Instruction = "merged",
                AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
            });

        // A real service provider carrying the owned-skill store (the factory resolves it lazily).
        var sp = new ServiceCollection()
            .AddSingleton<IAgentOwnedSkillStore>(_ownedStore)
            .BuildServiceProvider();

        _factory = new AgentFactory(
            NullLogger<AgentFactory>.Instance,
            monitor,
            Mock.Of<IDistributedCache>(),
            NullLoggerFactory.Instance,
            _contextFactory.Object,
            _skillRegistry.Object,
            chatClientFactory.Object,
            sp,
            new InMemorySkillCompletionTracker());
    }

    private static SkillDefinition Skill(string id, string marker) =>
        new() { Id = id, Name = id, Description = marker };

    [Fact]
    public async Task OwnedSkill_ShadowsGlobalSkillOfSameId_ForOwningAgent()
    {
        _skillRegistry.Setup(r => r.TryGet("shared")).Returns(Skill("shared", "GLOBAL"));
        _ownedStore.Register("agent-a", Skill("shared", "OWNED"));

        await _factory.CreateAgentFromSkillsAsync(
            ["shared"], new SkillAgentOptions { OwningAgentId = "agent-a" });

        _capturedSkills.Should().ContainSingle();
        _capturedSkills![0].Description.Should().Be("OWNED", "the owning agent's nested skill wins over the global one");
        _skillRegistry.Verify(r => r.TryGet("shared"), Times.Never, "the global registry must not even be consulted when the owner has the skill");
    }

    [Fact]
    public async Task OwnedSkill_IsInvisibleToAnotherAgent_FallsBackToGlobal()
    {
        _skillRegistry.Setup(r => r.TryGet("shared")).Returns(Skill("shared", "GLOBAL"));
        _ownedStore.Register("agent-a", Skill("shared", "OWNED"));

        await _factory.CreateAgentFromSkillsAsync(
            ["shared"], new SkillAgentOptions { OwningAgentId = "agent-b" });

        _capturedSkills![0].Description.Should().Be("GLOBAL", "agent-b owns no such skill, so the global one resolves");
    }

    [Fact]
    public async Task NoOwningAgentId_ResolvesFromGlobalRegistry()
    {
        _skillRegistry.Setup(r => r.TryGet("shared")).Returns(Skill("shared", "GLOBAL"));
        _ownedStore.Register("agent-a", Skill("shared", "OWNED"));

        await _factory.CreateAgentFromSkillsAsync(["shared"], new SkillAgentOptions());

        _capturedSkills![0].Description.Should().Be("GLOBAL");
    }

    [Fact]
    public async Task UnknownSkill_EvenWithOwner_Throws()
    {
        _skillRegistry.Setup(r => r.TryGet(It.IsAny<string>())).Returns((SkillDefinition?)null);

        var act = () => _factory.CreateAgentFromSkillsAsync(
            ["missing"], new SkillAgentOptions { OwningAgentId = "agent-a" });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*missing*not found*");
    }
}

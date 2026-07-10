using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Services.Bundles;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Tests.Fakes;
using Domain.AI.Agents;
using Domain.AI.Bundles;
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
/// Proves the bundle-overlay seam end-to-end through the real <see cref="AgentFactory"/>: with an
/// ephemeral overlay active, the factory resolves and builds an agent from a skill that exists <em>only</em>
/// in the overlay (absent from both the global registry and the persistent owned-skill store), and that
/// same build fails once the overlay is gone — so the overlay is demonstrably what makes the ephemeral
/// agent runnable, and it leaves nothing behind.
/// </summary>
public sealed class AgentFactoryEphemeralOverlayTests
{
    private readonly Mock<ISkillMetadataRegistry> _skillRegistry = new();
    private readonly Mock<AgentExecutionContextFactory> _contextFactory;
    private readonly AgentFactory _factory;
    private IReadOnlyList<SkillDefinition>? _capturedSkills;

    public AgentFactoryEphemeralOverlayTests()
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
                Name = "BundleAgent",
                Instruction = "merged",
                AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
            });

        // The factory resolves IAgentOwnedSkillStore lazily from the provider. Register the real
        // overlay-aware decorator over an empty persistent store, exactly as the composition root does.
        var sp = new ServiceCollection()
            .AddSingleton<IAgentOwnedSkillStore>(new OverlayAwareAgentOwnedSkillStore(new FakeOwnedSkillStore()))
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

    [Fact]
    public async Task OverlaySkill_ResolvesThroughFactory_OnlyWhileOverlayIsActive()
    {
        // The bundle skill exists in neither the global registry nor the persistent owned store.
        _skillRegistry.Setup(r => r.TryGet(It.IsAny<string>())).Returns((SkillDefinition?)null);

        var overlay = new EphemeralAgentOverlay
        {
            Agent = new AgentDefinition { Id = "bundle-agent", Name = "Bundle Agent" },
            OwnedSkills = [new SkillDefinition { Id = "bundle-skill", Name = "bundle-skill", Description = "OVERLAY" }],
        };

        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        {
            await _factory.CreateAgentFromSkillsAsync(
                ["bundle-skill"], new SkillAgentOptions { OwningAgentId = "bundle-agent" });
        }

        _capturedSkills.Should().ContainSingle();
        _capturedSkills![0].Description.Should().Be("OVERLAY",
            "the ephemeral agent's skill resolved from the overlay through the real factory");
    }

    [Fact]
    public async Task OverlaySkill_IsUnresolvable_OnceTheOverlayScopeEnds()
    {
        _skillRegistry.Setup(r => r.TryGet(It.IsAny<string>())).Returns((SkillDefinition?)null);

        // No overlay active — the same skill id cannot be resolved from any persistent source.
        var act = () => _factory.CreateAgentFromSkillsAsync(
            ["bundle-skill"], new SkillAgentOptions { OwningAgentId = "bundle-agent" });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*bundle-skill*not found*");
    }

    private sealed class FakeOwnedSkillStore : IAgentOwnedSkillStore
    {
        private readonly Dictionary<(string, string), SkillDefinition> _skills = new();

        public void Register(string agentId, SkillDefinition skill) => _skills[(agentId, skill.Id)] = skill;

        public SkillDefinition? TryGet(string agentId, string skillId) =>
            _skills.TryGetValue((agentId, skillId), out var s) ? s : null;

        public IReadOnlyList<SkillDefinition> GetForAgent(string agentId) =>
            _skills.Where(kv => kv.Key.Item1 == agentId).Select(kv => kv.Value).ToList();
    }
}

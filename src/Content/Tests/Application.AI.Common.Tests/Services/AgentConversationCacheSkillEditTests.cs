using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Context;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Services.Tools;
using Application.AI.Common.Tests.Fakes;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Tests.Common;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// A skill edited while a conversation is live (#792): the agent the conversation cached was built from the
/// old text, so the cache has to notice the skill changed and rebuild, through the same path as every
/// other build input.
/// </summary>
/// <remarks>
/// The registry reloads by handing out a fresh <see cref="SkillDefinition"/> instance, whether or not the
/// text changed, so the fresh-but-identical case is pinned as the control: a reload alone must not cost
/// the conversation its agent.
/// </remarks>
public sealed class AgentConversationCacheSkillEditTests
{
    private const string SkillId = "editable";
    private const string OwnerId = "owner-agent";

    private readonly Mock<ISkillMetadataRegistry> _registry = new();
    private readonly Mock<IAgentOwnedSkillStore> _ownedStore = new();
    private readonly AgentConversationCache _cache;

    public AgentConversationCacheSkillEditTests()
    {
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
        var services = new ServiceCollection()
            .AddSingleton(_ownedStore.Object)
            .BuildServiceProvider();
        var bare = new ServiceCollection().BuildServiceProvider();

        var contextFactory = new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            bare,
            NullLoggerFactory.Instance,
            new ToolChainBuilder(NullLogger<ToolChainBuilder>.Instance, bare, null),
            new SkillPrerequisiteResolver(),
            new UnsandboxedSkillFileReader(),
            Application.AI.Common.Tests.Governance.AdmissionHarness.PermissiveSanitizer(),
            Mock.Of<IAgentMetadataRegistry>(r => r.GetAll() == new List<Domain.AI.Agents.AgentDefinition>()));

        var completionTracker = new InMemorySkillCompletionTracker();
        var factory = new AgentFactory(
            NullLogger<AgentFactory>.Instance,
            monitor,
            Mock.Of<IDistributedCache>(),
            NullLoggerFactory.Instance,
            contextFactory,
            _registry.Object,
            new FakeChatClientFactory(),
            services,
            completionTracker);

        _cache = new AgentConversationCache(
            new MemoryCache(new MemoryCacheOptions()), factory, new ConversationRegistrationTracker(),
            completionTracker, _registry.Object, services, NullLogger<AgentConversationCache>.Instance);
    }

    private static SkillDefinition Skill(string instructions, string? contentHash) => new()
    {
        Id = SkillId,
        Name = SkillId,
        Instructions = instructions,
        ContentHash = contentHash
    };

    [Fact]
    public async Task GetOrCreateAsync_SkillEditedBetweenTurns_BuildsTheNextTurnFromTheNewText()
    {
        _registry.Setup(r => r.TryGet(SkillId)).Returns(Skill("Version one.", "hash-1"));
        var first = await _cache.GetOrCreateAsync("conv-edit", [SkillId], new SkillAgentOptions());

        _registry.Setup(r => r.TryGet(SkillId)).Returns(Skill("Version two.", "hash-2"));
        var second = await _cache.GetOrCreateAsync("conv-edit", [SkillId], new SkillAgentOptions());

        second.Should().NotBeSameAs(first, "the skill's text changed, so the cached agent is stale");
        _cache.TryGetContext("conv-edit")!.Instruction.Should().Contain("Version two.");
    }

    [Fact]
    public async Task GetOrCreateAsync_RegistryReloadedWithSameText_KeepsTheCachedAgent()
    {
        // Control, same instrument: a reload hands out a new instance, but the text is the same.
        _registry.Setup(r => r.TryGet(SkillId)).Returns(Skill("Version one.", "hash-1"));
        var first = await _cache.GetOrCreateAsync("conv-reload", [SkillId], new SkillAgentOptions());

        _registry.Setup(r => r.TryGet(SkillId)).Returns(Skill("Version one.", "hash-1"));
        var second = await _cache.GetOrCreateAsync("conv-reload", [SkillId], new SkillAgentOptions());

        second.Should().BeSameAs(first, "an unchanged skill must not cost the conversation its agent");
    }

    [Fact]
    public async Task GetOrCreateAsync_OwnedSkillEditedBetweenTurns_BuildsTheNextTurnFromTheNewText()
    {
        // The factory prefers the owning agent's own skill over the global one, so the cache has to
        // watch the skill the factory will actually use.
        _registry.Setup(r => r.TryGet(SkillId)).Returns(Skill("Global.", "global-hash"));
        var options = new SkillAgentOptions { OwningAgentId = OwnerId };

        _ownedStore.Setup(s => s.TryGet(OwnerId, SkillId)).Returns(Skill("Owned one.", "owned-1"));
        var first = await _cache.GetOrCreateAsync("conv-owned", [SkillId], options);

        _ownedStore.Setup(s => s.TryGet(OwnerId, SkillId)).Returns(Skill("Owned two.", "owned-2"));
        var second = await _cache.GetOrCreateAsync("conv-owned", [SkillId], options);

        second.Should().NotBeSameAs(first);
        _cache.TryGetContext("conv-owned")!.Instruction.Should().Contain("Owned two.");
    }

    [Fact]
    public async Task GetOrCreateAsync_GlobalSkillEditedWhileTheOwnerShadowsIt_KeepsTheCachedAgent()
    {
        // The shadowed global skill is not what the agent was built from, so editing it is no reason
        // to rebuild.
        _ownedStore.Setup(s => s.TryGet(OwnerId, SkillId)).Returns(Skill("Owned.", "owned-1"));
        var options = new SkillAgentOptions { OwningAgentId = OwnerId };

        _registry.Setup(r => r.TryGet(SkillId)).Returns(Skill("Global one.", "global-1"));
        var first = await _cache.GetOrCreateAsync("conv-shadow", [SkillId], options);

        _registry.Setup(r => r.TryGet(SkillId)).Returns(Skill("Global two.", "global-2"));
        var second = await _cache.GetOrCreateAsync("conv-shadow", [SkillId], options);

        second.Should().BeSameAs(first);
    }
}

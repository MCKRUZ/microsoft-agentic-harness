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
using Xunit;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// Integration tests for <see cref="AgentConversationCache"/> exercising the LIVE agent-build
/// path (real <see cref="AgentFactory"/> + real <see cref="AgentExecutionContextFactory"/>).
/// </summary>
/// <remarks>
/// Regression coverage for the prerequisite-scope bug: a skill declaring <c>prerequisites</c>
/// crashed on every conversation turn because <see cref="AgentFactory.ResolvePrerequisiteScope"/>
/// requires a conversation id under <see cref="AgentFactory.ConversationIdPropertyKey"/> in the
/// execution context, and nothing on the live path supplied it. The cache holds the
/// <c>conversationId</c> and is the only component positioned to flow it into
/// <see cref="SkillAgentOptions.AdditionalProperties"/> — these tests assert it does so WITHOUT
/// the caller hand-setting the key, which is the exact production scenario.
/// </remarks>
public sealed class AgentConversationCacheTests
{
    private const string ValidateSkillId = "validate";
    private const string DeploySkillId = "deploy";

    private readonly InMemorySkillCompletionTracker _completionTracker = new();
    private readonly ConversationRegistrationTracker _registrationTracker = new();
    private readonly IMemoryCache _memoryCache = new MemoryCache(new MemoryCacheOptions());
    private readonly AgentConversationCache _cache;

    public AgentConversationCacheTests()
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
        var services = new ServiceCollection().BuildServiceProvider();

        var contextFactory = new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            services,
            NullLoggerFactory.Instance,
            new ToolChainBuilder(NullLogger<ToolChainBuilder>.Instance, services, null, null),
            new SkillPrerequisiteResolver());

        // Registry returns a two-skill graph where "deploy" depends on "validate".
        var registry = new Mock<ISkillMetadataRegistry>();
        registry.Setup(r => r.TryGet(ValidateSkillId)).Returns(new SkillDefinition
        {
            Id = ValidateSkillId,
            Name = ValidateSkillId,
            Instructions = "Validate."
        });
        registry.Setup(r => r.TryGet(DeploySkillId)).Returns(new SkillDefinition
        {
            Id = DeploySkillId,
            Name = DeploySkillId,
            Instructions = "Deploy.",
            Prerequisites = new List<string> { ValidateSkillId }
        });

        var factory = new AgentFactory(
            NullLogger<AgentFactory>.Instance,
            monitor,
            Mock.Of<IDistributedCache>(),
            NullLoggerFactory.Instance,
            contextFactory,
            registry.Object,
            new FakeChatClientFactory(),
            services,
            _completionTracker);

        _cache = new AgentConversationCache(
            _memoryCache, factory, _registrationTracker, _completionTracker);
    }

    [Fact]
    public async Task GetOrCreateAsync_SkillWithPrerequisites_ResolvesScopeWithoutThrowing()
    {
        // Arrange — production shape: caller supplies NO conversationId in options.
        var options = new SkillAgentOptions();

        // Act — the cache must flow the conversationId into the agent build so the
        // prerequisite middleware can resolve its scope.
        var act = () => _cache.GetOrCreateAsync(
            "conv-live-path", [ValidateSkillId, DeploySkillId], options);

        // Assert — before the fix this throws InvalidOperationException ("no conversation scope").
        var agent = await act.Should().NotThrowAsync();
        agent.Subject.Should().NotBeNull();
    }

    [Fact]
    public async Task GetOrCreateAsync_DoesNotMutateCallerSuppliedOptions()
    {
        // Arrange — a shared options instance the caller may reuse for another conversation.
        var options = new SkillAgentOptions();

        // Act
        await _cache.GetOrCreateAsync("conv-x", [ValidateSkillId, DeploySkillId], options);

        // Assert — the cache injected the scope into a copy, never the caller's object.
        options.AdditionalProperties.Should().BeNull();
    }

    [Fact]
    public async Task Evict_ClearsPrerequisiteCompletionState()
    {
        // Arrange — build the agent, then record a completion under the same scope.
        await _cache.GetOrCreateAsync("conv-evict", [ValidateSkillId, DeploySkillId], new SkillAgentOptions());
        _completionTracker.MarkCompleted("conv-evict", ValidateSkillId);
        _completionTracker.IsCompleted("conv-evict", ValidateSkillId).Should().BeTrue();

        // Act
        _cache.Evict("conv-evict");

        // Assert — a re-created conversation with the same id starts clean.
        _completionTracker.IsCompleted("conv-evict", ValidateSkillId).Should().BeFalse();
    }
}

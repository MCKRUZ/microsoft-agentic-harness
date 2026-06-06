using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Identity;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Tests.Fakes;
using Domain.AI.Agents;
using Domain.AI.Identity;
using Domain.AI.Skills;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Identity;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using ScopedAgentExecutionContext = Application.AI.Common.Services.Agent.AgentExecutionContext;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Tests for <see cref="AgentFactory"/> covering provider availability checks,
/// delegation to <see cref="IChatClientFactory"/>, argument validation,
/// batch/category/tag agent creation, and error handling.
/// </summary>
public class AgentFactoryTests
{
    private readonly Mock<IChatClientFactory> _chatClientFactory;
    private readonly Mock<ISkillMetadataRegistry> _skillRegistry;
    private readonly Mock<AgentExecutionContextFactory> _contextFactory;
    private readonly AgentFactory _factory;

    public AgentFactoryTests()
    {
        _chatClientFactory = new Mock<IChatClientFactory>();
        _chatClientFactory
            .Setup(f => f.IsAvailable(It.IsAny<AIAgentFrameworkClientType>()))
            .Returns(true);
        _chatClientFactory
            .Setup(f => f.GetAvailableProviders())
            .Returns(new Dictionary<AIAgentFrameworkClientType, bool>
            {
                [AIAgentFrameworkClientType.AzureOpenAI] = true,
                [AIAgentFrameworkClientType.OpenAI] = false
            });
        _chatClientFactory
            .Setup(f => f.GetChatClientAsync(
                It.IsAny<AIAgentFrameworkClientType>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeChatClient());

        _skillRegistry = new Mock<ISkillMetadataRegistry>();

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

        // AgentExecutionContextFactory has many dependencies; mock it at the class level
        _contextFactory = new Mock<AgentExecutionContextFactory>(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance,
            null, null, null, null, null, null);

        _factory = new AgentFactory(
            NullLogger<AgentFactory>.Instance,
            monitor,
            Mock.Of<IDistributedCache>(),
            NullLoggerFactory.Instance,
            _contextFactory.Object,
            _skillRegistry.Object,
            _chatClientFactory.Object,
            new ServiceCollection().BuildServiceProvider(),
            new InMemorySkillCompletionTracker());
    }

    [Fact]
    public void IsProviderAvailable_DelegatesToChatClientFactory()
    {
        _chatClientFactory.Setup(f => f.IsAvailable(AIAgentFrameworkClientType.OpenAI)).Returns(false);

        var result = _factory.IsProviderAvailable(AIAgentFrameworkClientType.OpenAI);

        result.Should().BeFalse();
        _chatClientFactory.Verify(f => f.IsAvailable(AIAgentFrameworkClientType.OpenAI), Times.Once);
    }

    [Fact]
    public void GetAvailableProviders_DelegatesToChatClientFactory()
    {
        var result = _factory.GetAvailableProviders();

        result.Should().ContainKey(AIAgentFrameworkClientType.AzureOpenAI);
        result[AIAgentFrameworkClientType.AzureOpenAI].Should().BeTrue();
        result[AIAgentFrameworkClientType.OpenAI].Should().BeFalse();
    }

    [Fact]
    public async Task CreateAgentAsync_ProviderNotAvailable_ThrowsInvalidOperation()
    {
        _chatClientFactory.Setup(f => f.IsAvailable(It.IsAny<AIAgentFrameworkClientType>())).Returns(false);
        _chatClientFactory.Setup(f => f.GetAvailableProviders())
            .Returns(new Dictionary<AIAgentFrameworkClientType, bool>());

        var context = new AgentExecutionContext
        {
            Name = "test-agent",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
        };

        var act = () => _factory.CreateAgentAsync(context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public async Task CreateAgentAsync_PersistentAgents_RequiresAgentId()
    {
        var context = new AgentExecutionContext
        {
            Name = "test-agent",
            AIAgentFrameworkType = AIAgentFrameworkClientType.PersistentAgents,
            AgentId = null
        };

        var act = () => _factory.CreateAgentAsync(context);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*AgentId is required*");
    }

    [Fact]
    public async Task CreateAgentFromSkillAsync_SkillNotFound_ThrowsInvalidOperation()
    {
        _skillRegistry.Setup(r => r.TryGet("missing-skill")).Returns((SkillDefinition?)null);

        var act = () => _factory.CreateAgentFromSkillAsync("missing-skill");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing-skill*not found*");
    }

    [Fact]
    public async Task CreateAgentsFromSkillsAsync_SkillFailure_SkipsAndContinues()
    {
        // First skill fails, second succeeds
        _skillRegistry
            .Setup(r => r.TryGet("bad-skill"))
            .Returns((SkillDefinition?)null);

        var goodSkill = new SkillDefinition { Id = "good-skill", Name = "good-skill" };
        _skillRegistry
            .Setup(r => r.TryGet("good-skill"))
            .Returns(goodSkill);

        // Both will fail since CreateAgentFromSkillAsync needs full pipeline,
        // but the point is that exceptions are caught and logged
        var result = await _factory.CreateAgentsFromSkillsAsync(["bad-skill", "good-skill"]);

        // Both should fail gracefully (no exception thrown)
        result.Should().BeEmpty(); // Both fail because contextFactory isn't fully wired
    }

    [Fact]
    public async Task CreateAgentsByCategoryAsync_DelegatesToRegistry()
    {
        _skillRegistry
            .Setup(r => r.GetByCategory("analysis"))
            .Returns(new List<SkillDefinition>());

        var result = await _factory.CreateAgentsByCategoryAsync("analysis");

        result.Should().BeEmpty();
        _skillRegistry.Verify(r => r.GetByCategory("analysis"), Times.Once);
    }

    [Fact]
    public async Task CreateAgentsByTagsAsync_DelegatesToRegistry()
    {
        var tags = new[] { "research", "ai" };
        _skillRegistry
            .Setup(r => r.GetByTags(tags))
            .Returns(new List<SkillDefinition>());

        var result = await _factory.CreateAgentsByTagsAsync(tags);

        result.Should().BeEmpty();
        _skillRegistry.Verify(r => r.GetByTags(tags), Times.Once);
    }

    [Fact]
    public async Task CreateAgentAsync_ProviderNotAvailable_ErrorMessageListsAvailableProviders()
    {
        _chatClientFactory.Setup(f => f.IsAvailable(It.IsAny<AIAgentFrameworkClientType>())).Returns(false);
        _chatClientFactory.Setup(f => f.GetAvailableProviders())
            .Returns(new Dictionary<AIAgentFrameworkClientType, bool>
            {
                [AIAgentFrameworkClientType.OpenAI] = true,
                [AIAgentFrameworkClientType.AzureOpenAI] = false
            });

        var context = new AgentExecutionContext
        {
            Name = "test",
            AIAgentFrameworkType = AIAgentFrameworkClientType.Anthropic
        };

        var act = () => _factory.CreateAgentAsync(context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*OpenAI*");
    }

    [Fact]
    public async Task CreateAgentFromSkillsAsync_MultipleSkills_ResolvesAllFromRegistry()
    {
        var skill1 = new SkillDefinition { Id = "research", Name = "Research" };
        var skill2 = new SkillDefinition { Id = "make-ppt", Name = "Make PPT" };
        _skillRegistry.Setup(r => r.TryGet("research")).Returns(skill1);
        _skillRegistry.Setup(r => r.TryGet("make-ppt")).Returns(skill2);

        _contextFactory
            .Setup(f => f.MapToAgentContextAsync(
                It.Is<IReadOnlyList<SkillDefinition>>(s => s.Count == 2),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(new AgentExecutionContext
            {
                Name = "TestAgent",
                Instruction = "merged",
                AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
            });

        var agent = await _factory.CreateAgentFromSkillsAsync(
            ["research", "make-ppt"], new SkillAgentOptions());

        _skillRegistry.Verify(r => r.TryGet("research"), Times.Once);
        _skillRegistry.Verify(r => r.TryGet("make-ppt"), Times.Once);
    }

    [Fact]
    public async Task CreateAgentFromSkillsAsync_SkillNotFound_Throws()
    {
        _skillRegistry.Setup(r => r.TryGet("missing")).Returns((SkillDefinition?)null);

        var act = () => _factory.CreateAgentFromSkillsAsync(
            ["missing"], new SkillAgentOptions());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing*not found*");
    }

    [Fact]
    public async Task CreateAgentFromSkillsAsync_CyclicPrerequisites_Throws()
    {
        var skillA = new SkillDefinition { Id = "a", Name = "a", Prerequisites = ["b"] };
        var skillB = new SkillDefinition { Id = "b", Name = "b", Prerequisites = ["a"] };
        _skillRegistry.Setup(r => r.TryGet("a")).Returns(skillA);
        _skillRegistry.Setup(r => r.TryGet("b")).Returns(skillB);

        var act = () => _factory.CreateAgentFromSkillsAsync(["a", "b"], new SkillAgentOptions());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cycle*");
    }

    [Fact]
    public async Task CreateAgentFromSkillsAsync_PrerequisiteNotInSkillList_Throws()
    {
        var skill = new SkillDefinition { Id = "deploy", Name = "deploy", Prerequisites = ["validate"] };
        _skillRegistry.Setup(r => r.TryGet("deploy")).Returns(skill);

        var act = () => _factory.CreateAgentFromSkillsAsync(["deploy"], new SkillAgentOptions());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*validate*not in the agent's skill list*");
    }

    [Fact]
    public async Task CreateAgentFromSkillsAsync_ValidPrerequisiteChain_Succeeds()
    {
        var validate = new SkillDefinition { Id = "validate", Name = "validate", CompletionTool = "run_validation" };
        var deploy = new SkillDefinition { Id = "deploy", Name = "deploy", Prerequisites = ["validate"] };
        _skillRegistry.Setup(r => r.TryGet("validate")).Returns(validate);
        _skillRegistry.Setup(r => r.TryGet("deploy")).Returns(deploy);

        _contextFactory
            .Setup(f => f.MapToAgentContextAsync(
                It.Is<IReadOnlyList<SkillDefinition>>(s => s.Count == 2),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(new AgentExecutionContext
            {
                Name = "TestAgent",
                Instruction = "merged",
                AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
            });

        // Should not throw — prerequisites are valid
        var agent = await _factory.CreateAgentFromSkillsAsync(
            ["validate", "deploy"], new SkillAgentOptions());

        agent.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateAgentFromSkillsAsync_TransitiveCycle_Throws()
    {
        var a = new SkillDefinition { Id = "a", Name = "a", Prerequisites = ["c"] };
        var b = new SkillDefinition { Id = "b", Name = "b", Prerequisites = ["a"] };
        var c = new SkillDefinition { Id = "c", Name = "c", Prerequisites = ["b"] };
        _skillRegistry.Setup(r => r.TryGet("a")).Returns(a);
        _skillRegistry.Setup(r => r.TryGet("b")).Returns(b);
        _skillRegistry.Setup(r => r.TryGet("c")).Returns(c);

        var act = () => _factory.CreateAgentFromSkillsAsync(["a", "b", "c"], new SkillAgentOptions());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cycle*");
    }

    [Fact]
    public async Task CreateAgentFromSkillsAsync_NoPrerequisites_SkipsValidation()
    {
        var skill = new SkillDefinition { Id = "simple", Name = "simple" };
        _skillRegistry.Setup(r => r.TryGet("simple")).Returns(skill);

        _contextFactory
            .Setup(f => f.MapToAgentContextAsync(
                It.IsAny<IReadOnlyList<SkillDefinition>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(new AgentExecutionContext
            {
                Name = "TestAgent",
                Instruction = "test",
                AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
            });

        // Should not throw
        await _factory.CreateAgentFromSkillsAsync(["simple"], new SkillAgentOptions());
    }

    // --- Agent identity wiring (PR-1 step 4) ---------------------------------

    private AgentFactory BuildFactoryWithIdentityConfig(
        AgentIdentityConfig identity,
        IAgentIdentityResolver? resolver,
        IAgentExecutionContext? executionContext)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    DefaultDeployment = "gpt-4o",
                    ClientType = AIAgentFrameworkClientType.AzureOpenAI
                },
                Identity = identity
            }
        };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);

        var services = new ServiceCollection();
        if (resolver is not null) services.AddSingleton(resolver);
        if (executionContext is not null) services.AddSingleton(executionContext);
        var serviceProvider = services.BuildServiceProvider();

        return new AgentFactory(
            NullLogger<AgentFactory>.Instance,
            monitor,
            Mock.Of<IDistributedCache>(),
            NullLoggerFactory.Instance,
            _contextFactory.Object,
            _skillRegistry.Object,
            _chatClientFactory.Object,
            serviceProvider,
            new InMemorySkillCompletionTracker());
    }

    [Fact]
    public async Task CreateAgentAsync_IdentityDisabled_DoesNotResolveIdentity()
    {
        var resolver = new Mock<IAgentIdentityResolver>(MockBehavior.Strict);
        var execContext = new ScopedAgentExecutionContext();
        var factory = BuildFactoryWithIdentityConfig(
            new AgentIdentityConfig { Enabled = false },
            resolver.Object,
            execContext);

        var context = new AgentExecutionContext
        {
            Name = "test-agent",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
        };

        await factory.CreateAgentAsync(context);

        resolver.VerifyNoOtherCalls();
        execContext.AgentIdentity.Should().BeNull();
    }

    [Fact]
    public async Task CreateAgentAsync_IdentityEnabled_NoResolver_ThrowsInvalidOperation()
    {
        var factory = BuildFactoryWithIdentityConfig(
            new AgentIdentityConfig { Enabled = true, DefaultAudience = "api://x" },
            resolver: null,
            executionContext: new ScopedAgentExecutionContext());

        var context = new AgentExecutionContext
        {
            Name = "test-agent",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
        };

        var act = () => factory.CreateAgentAsync(context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IAgentIdentityResolver is*registered*");
    }

    [Fact]
    public async Task CreateAgentAsync_IdentityEnabled_NoExecutionContext_ThrowsInvalidOperation()
    {
        var resolver = new Mock<IAgentIdentityResolver>();
        resolver
            .Setup(r => r.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<AgentIdentity>.Success(new AgentIdentity
            {
                Id = "agent-1",
                Kind = AgentIdentityKind.ManagedIdentity
            }));

        var factory = BuildFactoryWithIdentityConfig(
            new AgentIdentityConfig { Enabled = true, DefaultAudience = "api://x" },
            resolver.Object,
            executionContext: null);

        var context = new AgentExecutionContext
        {
            Name = "test-agent",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
        };

        var act = () => factory.CreateAgentAsync(context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IAgentExecutionContext is*registered*");
    }

    [Fact]
    public async Task CreateAgentAsync_IdentityEnabled_ResolverSucceeds_StampsIdentityOnContext()
    {
        var resolvedIdentity = new AgentIdentity
        {
            Id = "agent-1",
            Kind = AgentIdentityKind.ManagedIdentity,
            TenantId = "tenant-a"
        };
        var resolver = new Mock<IAgentIdentityResolver>();
        resolver
            .Setup(r => r.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<AgentIdentity>.Success(resolvedIdentity));

        var execContext = new ScopedAgentExecutionContext();
        var factory = BuildFactoryWithIdentityConfig(
            new AgentIdentityConfig { Enabled = true, DefaultAudience = "api://x" },
            resolver.Object,
            execContext);

        var context = new AgentExecutionContext
        {
            Name = "test-agent",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
        };

        await factory.CreateAgentAsync(context);

        execContext.AgentIdentity.Should().Be(resolvedIdentity);
    }

    [Fact]
    public async Task CreateAgentAsync_IdentityEnabled_ResolverFails_ThrowsWithErrors()
    {
        var resolver = new Mock<IAgentIdentityResolver>();
        resolver
            .Setup(r => r.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<AgentIdentity>.Fail("agent_identity.no_provider_succeeded"));

        var factory = BuildFactoryWithIdentityConfig(
            new AgentIdentityConfig { Enabled = true, DefaultAudience = "api://x" },
            resolver.Object,
            executionContext: new ScopedAgentExecutionContext());

        var context = new AgentExecutionContext
        {
            Name = "test-agent",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
        };

        var act = () => factory.CreateAgentAsync(context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*resolution failed*")
            .WithMessage("*agent_identity.no_provider_succeeded*")
            .WithMessage("*test-agent*");
    }

    [Fact]
    public async Task CreateAgentAsync_IdentityEnabled_PassesAudienceAndScopesFromConfig()
    {
        CredentialContext? capturedContext = null;
        var resolver = new Mock<IAgentIdentityResolver>();
        resolver
            .Setup(r => r.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()))
            .Callback<CredentialContext, CancellationToken>((ctx, _) => capturedContext = ctx)
            .ReturnsAsync(Result<AgentIdentity>.Success(new AgentIdentity
            {
                Id = "agent-1",
                Kind = AgentIdentityKind.ManagedIdentity
            }));

        var factory = BuildFactoryWithIdentityConfig(
            new AgentIdentityConfig
            {
                Enabled = true,
                DefaultAudience = "api://my-agent",
                DefaultScopes = ["api://my-agent/.default", "https://graph.microsoft.com/.default"]
            },
            resolver.Object,
            new ScopedAgentExecutionContext());

        var context = new AgentExecutionContext
        {
            Name = "test-agent",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
        };

        await factory.CreateAgentAsync(context);

        capturedContext.Should().NotBeNull();
        capturedContext!.Audience.Should().Be("api://my-agent");
        capturedContext.Scopes.Should().HaveCount(2);
        capturedContext.Scopes.Should().Contain("api://my-agent/.default");
        capturedContext.Scopes.Should().Contain("https://graph.microsoft.com/.default");
    }

    [Fact]
    public async Task CreateAgentAsync_IdentityEnabled_TwiceWithSameIdentity_DoesNotThrow()
    {
        // Re-entrant agent construction (e.g. CreateAgentFromSkillsAsync calls CreateAgentAsync)
        // must not trip the identity-conflict guard when the resolver returns the same value.
        var resolvedIdentity = new AgentIdentity
        {
            Id = "agent-1",
            Kind = AgentIdentityKind.ManagedIdentity
        };
        var resolver = new Mock<IAgentIdentityResolver>();
        resolver
            .Setup(r => r.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<AgentIdentity>.Success(resolvedIdentity));

        var execContext = new ScopedAgentExecutionContext();
        var factory = BuildFactoryWithIdentityConfig(
            new AgentIdentityConfig { Enabled = true, DefaultAudience = "api://x" },
            resolver.Object,
            execContext);

        var context = new AgentExecutionContext
        {
            Name = "test-agent",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
        };

        await factory.CreateAgentAsync(context);
        await factory.CreateAgentAsync(context);

        execContext.AgentIdentity.Should().Be(resolvedIdentity);
    }
}

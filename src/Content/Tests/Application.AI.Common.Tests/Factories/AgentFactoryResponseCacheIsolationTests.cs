using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Tests.Fakes;
using Domain.AI.Agents;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Tests.Common;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// The chat response cache an agent is built with must never serve one caller's answer to another.
/// </summary>
/// <remarks>
/// <para>
/// Before this, the cache key was built from the conversation and the chat options alone. Two callers who sent the
/// same conversation to the same agent shared one entry: a second user, or a user in another tenant, got the first
/// caller's stored answer back without the model being called. With a Redis-backed cache that crossed hosts too. The
/// answer itself is derived from the same input, but the hit is observable (milliseconds against seconds), so any
/// caller could learn whether anyone else had already asked a given question, and the cache, which the retention
/// work treats as holding personal data, crossed the tenant boundary.
/// </para>
/// <para>
/// Each test drives the real <see cref="AgentFactory"/> pipeline against a real in-memory distributed cache and counts
/// how many requests reach the model, which is what a cache hit or miss actually changes.
/// </para>
/// </remarks>
public sealed class AgentFactoryResponseCacheIsolationTests
{
    private readonly FakeChatClient _model = new FakeChatClient().WithDefaultResponse("Paris.");
    private readonly IDistributedCache _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly AmbientRequestScope _ambientScope = new();
    private readonly AppConfig _config = new()
    {
        AI = new AIConfig
        {
            AgentFramework = new AgentFrameworkConfig
            {
                DefaultDeployment = "gpt-4o",
                ClientType = AIAgentFrameworkClientType.AzureOpenAI,
            },
        },
    };

    [Fact]
    public async Task TheSameQuestion_FromAnotherUserInTheSameTenant_ReachesTheModel()
    {
        await AskAs(new Caller("user-a", "tenant-1"));
        await AskAs(new Caller("user-b", "tenant-1"));

        _model.RequestHistory.Should().HaveCount(2, "a second user must never be served the first user's cached answer");
    }

    [Fact]
    public async Task TheSameQuestion_FromAnotherTenant_ReachesTheModel()
    {
        await AskAs(new Caller("user-a", "tenant-1"));
        await AskAs(new Caller("user-a", "tenant-2"));

        _model.RequestHistory.Should().HaveCount(2,
            "a cached answer must never cross the tenant boundary, even for an identical user id");
    }

    [Fact]
    public async Task TheSameQuestion_FromTheSameCaller_IsServedFromTheCache()
    {
        await AskAs(new Caller("user-a", "tenant-1"));
        await AskAs(new Caller("user-a", "tenant-1"));

        _model.RequestHistory.Should().HaveCount(1, "the cache must still work for the caller who wrote the entry");
    }

    [Fact]
    public async Task AQuestion_WithNoKnownCaller_IsNeitherCachedNorServedFromTheCache()
    {
        await AskAs(caller: null);
        await AskAs(caller: null);

        _model.RequestHistory.Should().HaveCount(2,
            "an answer that cannot be attributed to a caller must not be shared between unattributed requests");
    }

    [Fact]
    public async Task OneSharedAgent_AskedByTwoUsers_ReachesTheModelForEach()
    {
        // Agents are cached per conversation and reused across requests, so the caller must be read on each call,
        // never captured when the agent and its cache were built.
        AIAgent agent;
        using (BeginRequest(new Caller("user-a", "tenant-1")))
            agent = await CreateAgent();

        using (BeginRequest(new Caller("user-a", "tenant-1")))
            await agent.RunAsync(Question);
        using (BeginRequest(new Caller("user-b", "tenant-1")))
            await agent.RunAsync(Question);

        _model.RequestHistory.Should().HaveCount(2, "a shared agent must not serve the user it was built for to everyone");
    }

    private const string Question = "What is the capital of France?";

    private async Task AskAs(Caller? caller)
    {
        using var scope = BeginRequest(caller);
        var agent = await CreateAgent();
        await agent.RunAsync(Question);
    }

    private IDisposable BeginRequest(Caller? caller)
    {
        var requestServices = new ServiceCollection();
        if (caller is not null)
            requestServices.AddSingleton<IKnowledgeScope>(caller);
        return _ambientScope.BeginScope(requestServices.BuildServiceProvider());
    }

    private Task<AIAgent> CreateAgent() => CreateFactory().CreateAgentAsync(new AgentExecutionContext
    {
        Name = "agent",
        AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI,
    });

    private AgentFactory CreateFactory()
    {
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == _config);
        var chatClients = new Mock<IChatClientFactory>();
        chatClients.Setup(f => f.IsAvailable(It.IsAny<AIAgentFrameworkClientType>())).Returns(true);
        chatClients.Setup(f => f.GetChatClientAsync(It.IsAny<AIAgentFrameworkClientType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_model);

        var contextFactory = new Mock<AgentExecutionContextFactory>(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance,
            null!, null!, new UnsandboxedSkillFileReader(), null!,
            Mock.Of<IAgentMetadataRegistry>(r => r.GetAll() == new List<AgentDefinition>()),
            null!, null!, null!, null!);

        var services = new ServiceCollection()
            .AddSingleton<IAmbientRequestScope>(_ambientScope)
            .BuildServiceProvider();

        return new AgentFactory(
            NullLogger<AgentFactory>.Instance,
            monitor,
            _cache,
            NullLoggerFactory.Instance,
            contextFactory.Object,
            Mock.Of<ISkillMetadataRegistry>(),
            chatClients.Object,
            services,
            new InMemorySkillCompletionTracker());
    }

    private sealed record Caller(string? UserId, string? TenantId) : IKnowledgeScope
    {
        public string? DatasetId => null;
        public string? DatasetName => null;
        public string? DatasetOwnerId => null;
        public string? AgentId => "agent";
        public string? ConversationId => null;
    }
}

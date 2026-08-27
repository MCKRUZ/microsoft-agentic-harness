using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Traces;
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
/// crashed on every conversation turn because <c>AgentFactory.ResolvePrerequisiteScope</c>
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
            new ToolChainBuilder(NullLogger<ToolChainBuilder>.Instance, services, null),
            new SkillPrerequisiteResolver(),
            new UnsandboxedSkillFileReader(),
            // #480: GoverningToolContextProvider now resolves a sanitizer to scrub load_skill/
            // read_skill_resource output, so the live-agent-build path this fixture exercises needs
            // one, matching what every real composition already provides unconditionally.
            Application.AI.Common.Tests.Governance.AdmissionHarness.PermissiveSanitizer());

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
            _memoryCache, factory, _registrationTracker, _completionTracker,
            NullLogger<AgentConversationCache>.Instance);
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
    public async Task GetOrCreateAsync_SameConversationId_ReturnsCachedAgentWithoutConsultingSkills()
    {
        // The cache is keyed SOLELY by conversation id: a hit short-circuits before the requested
        // skills or options are looked at AT ALL. The second request here asks for "deploy" on its
        // own, which is an invalid skill set — building it throws because its "validate" prerequisite
        // is absent (see GetOrCreateAsync_SkillWithPrerequisites_ResolvesScopeWithoutThrowing). That
        // it returns an agent anyway, rather than throwing, proves the requested skills were never
        // examined.
        //
        // This is why concurrent plan steps must never share a conversation id: the second step gets
        // a cache HIT and silently runs under the first step's agent — its skills, instructions,
        // allowed tools and deployment. Proven here against the real cache so the plan engine's
        // per-step id derivation (PlanRunKeys.StepConversationId) has a demonstrated reason to exist.
        var first = await _cache.GetOrCreateAsync("conv-shared", [ValidateSkillId], new SkillAgentOptions());

        var second = await _cache.GetOrCreateAsync("conv-shared", [DeploySkillId], new SkillAgentOptions());

        second.Should().BeSameAs(first,
            "a cache hit returns the existing agent even for a skill set that could not have been built");
    }

    [Fact]
    public async Task GetOrCreateAsync_DistinctConversationIds_BuildIndependentAgents()
    {
        // The converse, and the property the plan engine relies on: distinct ids never collide, so
        // per-step ids keep agent resolution, eviction and skill tracking isolated per step.
        var first = await _cache.GetOrCreateAsync(
            "conv-a", [ValidateSkillId], new SkillAgentOptions());
        var second = await _cache.GetOrCreateAsync(
            "conv-b", [ValidateSkillId, DeploySkillId], new SkillAgentOptions());

        second.Should().NotBeSameAs(first);
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

/// <summary>
/// Proves an execution-trace writer created for a conversation is finalized when that
/// conversation's context leaves the cache (#505).
/// </summary>
/// <remarks>
/// <para>
/// <c>ITraceWriter.CompleteAsync</c> stamps <c>write_completed</c> into the run manifest and its
/// disposal releases a semaphore and a file handle. Only the meta-harness evaluation loop did this;
/// the conversation path created writers and abandoned them, so every production run stayed flagged
/// incomplete to any reader honouring that flag. It went unnoticed because nothing wrote to those
/// writers until #505 connected them to the middleware.
/// </para>
/// <para>
/// The hook is the cache entry's own post-eviction callback rather than <c>Evict</c>, because the
/// cache also drops entries on sliding expiry: a conversation that simply goes idle never calls
/// <c>Evict</c>, and hanging cleanup off the explicit call alone would leak every abandoned one.
/// The second test is what pins that distinction — delete the callback registration and it fails
/// while an Evict-only implementation would still pass the first.
/// </para>
/// </remarks>
public sealed class AgentConversationCacheTraceLifecycleTests
{
    private const string SkillId = "trace-skill";

    private static (AgentConversationCache Cache, Mock<ITraceWriter> Writer, IMemoryCache Memory,
        Task Finalized) Create()
    {
        var appConfig = new AppConfig
        {
            MetaHarness = new Domain.Common.Config.MetaHarness.MetaHarnessConfig
            {
                ExecutionTracingEnabled = true
            },
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

        // IMemoryCache queues post-eviction callbacks to the thread pool, so finalization does not
        // happen before Remove/Evict returns. Signalling off the last call the callback makes lets
        // each test await the work instead of racing it — without this the assertions pass or fail
        // on timing, which is worse than not having them.
        var finalized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writer = new Mock<ITraceWriter>();
        writer.SetupGet(w => w.Scope)
            .Returns(Domain.Common.MetaHarness.TraceScope.ForExecution(Guid.NewGuid()));
        writer.Setup(w => w.CompleteAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        writer.Setup(w => w.DisposeAsync())
            .Returns(ValueTask.CompletedTask)
            .Callback(() => finalized.TrySetResult());

        var traceStore = new Mock<Application.AI.Common.Interfaces.Traces.IExecutionTraceStore>();
        traceStore
            .Setup(s => s.StartRunAsync(
                It.IsAny<Domain.Common.MetaHarness.TraceScope>(),
                It.IsAny<Domain.Common.MetaHarness.RunMetadata>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(writer.Object);

        var contextFactory = new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            services,
            NullLoggerFactory.Instance,
            new ToolChainBuilder(NullLogger<ToolChainBuilder>.Instance, services, null),
            new SkillPrerequisiteResolver(),
            new UnsandboxedSkillFileReader(),
            Application.AI.Common.Tests.Governance.AdmissionHarness.PermissiveSanitizer(),
            traceStore: traceStore.Object);

        var registry = new Mock<ISkillMetadataRegistry>();
        registry.Setup(r => r.TryGet(SkillId)).Returns(new SkillDefinition
        {
            Id = SkillId,
            Name = SkillId,
            Instructions = "Do the thing."
        });

        var completionTracker = new InMemorySkillCompletionTracker();
        var memory = new MemoryCache(new MemoryCacheOptions());

        var factory = new AgentFactory(
            NullLogger<AgentFactory>.Instance,
            monitor,
            Mock.Of<IDistributedCache>(),
            NullLoggerFactory.Instance,
            contextFactory,
            registry.Object,
            new FakeChatClientFactory(),
            services,
            completionTracker);

        var cache = new AgentConversationCache(
            memory, factory, new ConversationRegistrationTracker(), completionTracker,
            NullLogger<AgentConversationCache>.Instance);

        return (cache, writer, memory, finalized.Task);
    }

    /// <summary>Awaits the eviction callback, failing loudly rather than hanging if it never runs.</summary>
    private static async Task AwaitFinalization(Task finalized)
    {
        var completed = await Task.WhenAny(finalized, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(finalized,
            "the post-eviction callback must run — a timeout here means nothing finalizes the writer");
    }

    [Fact]
    public async Task Evict_CompletesAndDisposesTheConversationsTraceWriter()
    {
        var (cache, writer, _, finalized) = Create();
        await cache.GetOrCreateAsync("conv-trace", [SkillId], new SkillAgentOptions());

        cache.Evict("conv-trace");
        await AwaitFinalization(finalized);

        writer.Verify(w => w.CompleteAsync(It.IsAny<CancellationToken>()), Times.Once,
            "an abandoned writer leaves the run manifest permanently marked incomplete");
        writer.Verify(w => w.DisposeAsync(), Times.Once,
            "the writer holds a semaphore and a file handle that must be released");
    }

    [Fact]
    public async Task CacheEntryRemovedWithoutEvict_StillCompletesTheTraceWriter()
    {
        // The idle path: entries also leave on sliding expiry, which never routes through Evict.
        var (cache, writer, memory, finalized) = Create();
        await cache.GetOrCreateAsync("conv-idle", [SkillId], new SkillAgentOptions());

        memory.Remove("conv-idle::context");
        await AwaitFinalization(finalized);

        writer.Verify(w => w.CompleteAsync(It.IsAny<CancellationToken>()), Times.Once,
            "cleanup must hang off the cache entry, not off the explicit eviction call");
    }
}

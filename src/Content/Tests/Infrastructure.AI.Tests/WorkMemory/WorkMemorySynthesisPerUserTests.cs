using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Learnings;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.WorkMemory;
using Application.AI.Common.Services;
using Application.Core.CQRS.Learnings;
using Domain.AI.Governance;
using Domain.AI.Learnings;
using Domain.AI.RAG.Models;
using Domain.AI.WorkMemory;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI.Learnings;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Infrastructure.AI.KnowledgeGraph.Learnings;
using Infrastructure.AI.KnowledgeGraph.Memory;
using Infrastructure.AI.KnowledgeGraph.Provenance;
using Infrastructure.AI.KnowledgeGraph.Scoping;
using Infrastructure.AI.KnowledgeGraph.WorkMemory;
using Infrastructure.AI.Learnings;
using Infrastructure.AI.WorkMemory;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.WorkMemory;

/// <summary>
/// Issue #968, end to end: a lesson work-memory synthesis distils from one user's conversations must
/// reach only that user, be owner-stamped so owner-scoped erasure finds it, and the pass must work with
/// multi-tenant isolation on as well as off.
/// </summary>
/// <remarks>
/// Everything is real (graph store, episode store, learnings store, the remember handler behind its
/// validator, the recall handler, the scope accessor) except the synthesizer, the one LLM boundary,
/// which echoes each episode's user message into a lesson so a lesson's text names its source.
/// </remarks>
public sealed class WorkMemorySynthesisPerUserTests
{
    private const string UserA = "user-a";
    private const string UserB = "user-b";

    private readonly AppConfig _appConfig = new();
    private readonly InMemoryGraphStore _inner = new(NullLogger<InMemoryGraphStore>.Instance);
    private readonly AmbientRequestScope _ambient = new();
    private readonly EchoSynthesizer _synthesizer = new();

    public WorkMemorySynthesisPerUserTests()
    {
        _appConfig.AI.WorkMemory.Enabled = true;
        _appConfig.AI.WorkMemory.SynthesisEnabled = true;
        _appConfig.AI.WorkMemory.SynthesisLookbackHours = 24;
        _appConfig.AI.WorkMemory.MaxEpisodesPerRun = 200;
        _appConfig.AI.WorkMemory.MinConfidenceToStore = 0.5;
        _appConfig.AI.Learnings.Enabled = true;
    }

    [Fact]
    public async Task Synthesis_UserAsEpisode_ReachesUserAsRecallOnly()
    {
        var host = BuildHost(isolation: false);
        await SeedEpisodeAsync(host, UserA, "alpha-task");

        (await RunPassAsync(host)).Should().Be(1);

        (await RecallAsync(host, UserA)).Should().ContainSingle(c => c.Contains("alpha-task"));
        (await RecallAsync(host, UserB)).Should().NotContain(c => c.Contains("alpha-task"));
        (await RecallAsync(host, null)).Should().NotContain(c => c.Contains("alpha-task"));
    }

    [Fact]
    public async Task Synthesis_UserAsEpisode_LessonIsFoundByOwnerErasureLookup()
    {
        var host = BuildHost(isolation: false);
        await SeedEpisodeAsync(host, UserA, "alpha-task");

        await RunPassAsync(host);

        var owned = await _inner.GetNodesByOwnerAsync(UserA);
        owned.Should().Contain(n => n.Type == "LearningEntry" && n.Properties["Content"].Contains("alpha-task"));
    }

    [Fact]
    public async Task Synthesis_IsolationOn_ProcessesOwnerStampedEpisodes()
    {
        var host = BuildHost(isolation: true);
        await SeedEpisodeAsync(host, UserA, "alpha-task");

        (await RunPassAsync(host)).Should().Be(1);

        _synthesizer.Calls.Should().ContainSingle()
            .Which.Should().ContainSingle(e => e.UserMessage == "alpha-task");
        (await RecallAsync(host, UserA)).Should().ContainSingle(c => c.Contains("alpha-task"));
        (await RecallAsync(host, UserB)).Should().BeEmpty();
    }

    [Fact]
    public async Task Synthesis_TwoOwners_EachSynthesizerCallSeesOnlyOneOwnersEpisodes()
    {
        var host = BuildHost(isolation: false);
        await SeedEpisodeAsync(host, UserA, "alpha-task");
        await SeedEpisodeAsync(host, UserB, "bravo-task");

        (await RunPassAsync(host)).Should().Be(2);

        _synthesizer.Calls.Should().HaveCount(2);
        _synthesizer.Calls.Should().OnlyContain(call => call.Select(e => e.OwnerId).Distinct().Count() == 1);
        (await RecallAsync(host, UserA)).Should().Contain(c => c.Contains("alpha-task"))
            .And.NotContain(c => c.Contains("bravo-task"));
        (await RecallAsync(host, UserB)).Should().Contain(c => c.Contains("bravo-task"))
            .And.NotContain(c => c.Contains("alpha-task"));
    }

    [Fact]
    public async Task Synthesis_OwnerlessEpisodes_BecomeGlobalLessonsEveryoneRecalls()
    {
        var host = BuildHost(isolation: false);
        await SeedEpisodeAsync(host, null, "system-task");

        (await RunPassAsync(host)).Should().Be(1);

        (await RecallAsync(host, UserA)).Should().Contain(c => c.Contains("system-task"));
        (await RecallAsync(host, UserB)).Should().Contain(c => c.Contains("system-task"));
        (await _inner.GetNodesByOwnerAsync(UserA)).Should().NotContain(n => n.Type == "LearningEntry");
    }

    [Fact]
    public async Task Synthesis_HostWithoutScopeWriter_SkipsOwnerGroupInsteadOfWritingItShared()
    {
        var host = BuildHost(isolation: false);
        await SeedEpisodeAsync(host, UserA, "alpha-task");
        var noWriterHost = BuildHost(isolation: false, registerScopeWriter: false);

        (await RunPassAsync(noWriterHost)).Should().Be(0);

        _synthesizer.Calls.Should().BeEmpty();
        (await RecallAsync(host, UserB)).Should().BeEmpty();
    }

    private ServiceProvider BuildHost(bool isolation, bool registerScopeWriter = true)
    {
        _appConfig.AI.Rag.GraphRag.MultiTenantIsolation = isolation;
        var options = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == _appConfig);
        IKnowledgeGraphStore graph = isolation
            ? new TenantIsolatedGraphStore(_inner, _ambient, new KnowledgeScopeValidator(options),
                NullLogger<TenantIsolatedGraphStore>.Instance)
            : _inner;
        var learnings = new GraphLearningsStore(graph, _ambient, NullLogger<GraphLearningsStore>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<IAmbientRequestScope>(_ambient);
        services.AddSingleton(graph);
        services.AddSingleton<ILearningsStore>(learnings);
        services.AddSingleton<IWorkEpisodeStore>(
            new GraphWorkEpisodeStore(graph, _ambient, NullLogger<GraphWorkEpisodeStore>.Instance));
        services.AddSingleton<IWorkEpisodeSynthesizer>(_synthesizer);
        services.AddSingleton(Mock.Of<IAgentExecutionContext>());
        services.AddScoped<KnowledgeScopeAccessor>();
        services.AddScoped<IKnowledgeScope>(sp => sp.GetRequiredService<KnowledgeScopeAccessor>());
        if (registerScopeWriter)
            services.AddScoped<IKnowledgeScopeWriter>(sp => sp.GetRequiredService<KnowledgeScopeAccessor>());
        services.AddScoped(_ => BuildMediator(learnings, options));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Routes <see cref="RememberCommand"/> through the real validator and the real handler, so a
    /// lesson the validator would reject in production is rejected here too.
    /// </summary>
    private static IMediator BuildMediator(ILearningsStore store, IOptionsMonitor<AppConfig> options)
    {
        var gate = new ProvenanceMemoryWriteGate(
            new DefaultProvenanceStamper(options, TimeProvider.System),
            new NoOpMemoryIntentClassifier(),
            options,
            NullLogger<ProvenanceMemoryWriteGate>.Instance,
            scanner: Mock.Of<IPromptInjectionScanner>(s => s.Scan(It.IsAny<string>()) == InjectionScanResult.Clean()),
            audit: null);
        var handler = new RememberCommandHandler(store, Mock.Of<ILearningNotificationChannel>(), gate, options,
            TimeProvider.System, NullLogger<RememberCommandHandler>.Instance);
        var validator = new RememberCommandValidator();

        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<RememberCommand>(), It.IsAny<CancellationToken>()))
            .Returns<RememberCommand, CancellationToken>(async (command, ct) =>
            {
                var validation = await validator.ValidateAsync(command, ct);
                return validation.IsValid
                    ? await handler.Handle(command, ct)
                    : Result<LearningEntry>.Fail(validation.Errors.Select(e => e.ErrorMessage).ToArray());
            });
        return mediator.Object;
    }

    /// <summary>Saves an episode as <paramref name="userId"/> would in a request, or owner-less when null.</summary>
    private async Task SeedEpisodeAsync(ServiceProvider host, string? userId, string message)
    {
        await using var scope = host.CreateAsyncScope();
        using var ambient = userId is null ? null : _ambient.BeginScope(scope.ServiceProvider);
        using var identity = userId is null
            ? null
            : scope.ServiceProvider.GetRequiredService<IKnowledgeScopeWriter>().SetScope(userId);

        var result = await scope.ServiceProvider.GetRequiredService<IWorkEpisodeStore>()
            .SaveAsync(Episode(message), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
    }

    private async Task<int> RunPassAsync(ServiceProvider host)
    {
        var sut = new WorkMemorySynthesisBackgroundService(
            host.GetRequiredService<IServiceScopeFactory>(),
            _ambient,
            host.GetRequiredService<IOptionsMonitor<AppConfig>>(),
            TimeProvider.System,
            NullLogger<WorkMemorySynthesisBackgroundService>.Instance);

        var result = await sut.SynthesizeNowAsync(CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }

    /// <summary>Recalls through the real recall handler as <paramref name="userId"/> (null = unknown caller).</summary>
    private async Task<IReadOnlyList<string>> RecallAsync(ServiceProvider host, string? userId)
    {
        await using var scope = host.CreateAsyncScope();
        using var ambient = _ambient.BeginScope(scope.ServiceProvider);
        using var identity = scope.ServiceProvider.GetRequiredService<KnowledgeScopeAccessor>().SetScope(userId);
        var options = host.GetRequiredService<IOptionsMonitor<AppConfig>>();
        var store = host.GetRequiredService<ILearningsStore>();

        var handler = new RecallQueryHandler(
            store,
            new DefaultLearningDecayService(store,
                Mock.Of<IOptionsMonitor<LearningsConfig>>(m => m.CurrentValue == new LearningsConfig()),
                TimeProvider.System, NullLogger<DefaultLearningDecayService>.Instance),
            new ConstantEmbeddingService(),
            host.GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            NullLogger<RecallQueryHandler>.Instance,
            scope.ServiceProvider.GetRequiredService<IKnowledgeScope>());

        var result = await handler.Handle(new RecallQuery
        {
            Context = "anything",
            Scope = new LearningScope { IsGlobal = true },
            MaxResults = 50,
            RecordAccess = false
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        return result.Value!.Select(w => w.Learning.Content).ToList();
    }

    private static WorkEpisode Episode(string message) => new()
    {
        EpisodeId = Guid.NewGuid(),
        AgentId = "agent",
        ConversationId = $"conv-{message}",
        TurnNumber = 1,
        UserMessage = message,
        ResponseSummary = "done",
        Outcome = EpisodeOutcome.Success,
        InputTokens = 10,
        OutputTokens = 5,
        CreatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>Turns each episode into one lesson naming its user message, and records every call.</summary>
    private sealed class EchoSynthesizer : IWorkEpisodeSynthesizer
    {
        private readonly List<IReadOnlyList<WorkEpisode>> _calls = [];

        public IReadOnlyList<IReadOnlyList<WorkEpisode>> Calls => _calls;

        public Task<IReadOnlyList<SynthesizedLesson>> SynthesizeAsync(
            IReadOnlyList<WorkEpisode> episodes, CancellationToken ct)
        {
            _calls.Add(episodes.ToList());
            IReadOnlyList<SynthesizedLesson> lessons = episodes
                .Select(e => new SynthesizedLesson
                {
                    Content = $"When asked about {e.UserMessage}, check the inputs first.",
                    Category = LearningCategory.DomainKnowledge,
                    Confidence = 0.9
                })
                .ToList();
            return Task.FromResult(lessons);
        }
    }

    /// <summary>One vector for every input, so relevance never decides what is recalled.</summary>
    private sealed class ConstantEmbeddingService : IEmbeddingService
    {
        private static readonly ReadOnlyMemory<float> Vector = new([1f, 0f, 0f]);

        public Task<ReadOnlyMemory<float>> EmbedQueryAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(Vector);

        public Task<IReadOnlyList<DocumentChunk>> EmbedAsync(
            IReadOnlyList<DocumentChunk> chunks, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}

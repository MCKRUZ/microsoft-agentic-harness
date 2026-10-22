using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Compliance;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Compliance;

/// <summary>
/// Knowledge-graph retention (#901): nodes past a stamped expiry go, personal knowledge goes once it has not been written
/// for the personal period, document-derived knowledge and learnings stay, and the run happens on the configured interval.
/// </summary>
public sealed class RetentionEnforcementServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly InMemoryGraphStore _store = new(Mock.Of<ILogger<InMemoryGraphStore>>());
    private readonly Mock<IErasureOrchestrator> _orchestrator = new();
    private readonly AppConfig _config = new();
    private IReadOnlyList<GraphNode>? _expired;

    public RetentionEnforcementServiceTests()
    {
        _orchestrator.Setup(e => e.ExpireNodesAsync(It.IsAny<IReadOnlyList<GraphNode>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<GraphNode>, CancellationToken>((nodes, _) => _expired = nodes)
            .ReturnsAsync((IReadOnlyList<GraphNode> nodes, CancellationToken _) => Receipt(nodes.Count));
    }

    private RetentionEnforcementService Create(TimeProvider? time = null)
    {
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(() => _config);
        var services = new ServiceCollection();
        services.AddScoped(_ => _orchestrator.Object);
        return new RetentionEnforcementService(
            _store, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), monitor.Object,
            time ?? TimeProvider.System, NullLogger<RetentionEnforcementService>.Instance);
    }

    private IEnumerable<string> ExpiredIds => _expired?.Select(n => n.Id) ?? [];

    [Fact]
    public async Task ANodePastItsStampedExpiry_IsRemoved_AndAValidOneKept()
    {
        await _store.AddNodesAsync([
            Node("expired", "Fact", owner: "u1", created: Now.AddDays(-400), expires: Now.AddDays(-35)),
            Node("valid", "Fact", owner: "u1", created: Now, expires: Now.AddDays(365)),
        ]);

        (await Create().EnforceRetentionAsync(Now, CancellationToken.None)).Should().Be(1);

        ExpiredIds.Should().Equal("expired");
    }

    [Fact]
    public async Task PersonalKnowledge_NotWrittenForThePeriod_IsRemoved_EvenWithNoStampedExpiry()
    {
        // Written before the rule existed, so nothing stamped an expiry on it.
        await _store.AddNodesAsync([
            Node("old-memory", "Preference", owner: "u1", created: Now.AddDays(-731)),
            Node("recent-memory", "Preference", owner: "u1", created: Now.AddDays(-729)),
        ]);

        await Create().EnforceRetentionAsync(Now, CancellationToken.None);

        ExpiredIds.Should().Equal("old-memory");
    }

    [Fact]
    public async Task KnowledgeExtractedFromADocument_IsKeptAsLongAsItsDocument()
    {
        await _store.AddNodesAsync([
            Node("entity", "Organization", owner: "u1", created: Now.AddDays(-2000), chunks: ["doc_chunk_0"]),
        ]);

        (await Create().EnforceRetentionAsync(Now, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task SharedKnowledge_WithNoOwner_IsNotPersonal()
    {
        await _store.AddNodesAsync([Node("shared", "Concept2", owner: null, created: Now.AddDays(-2000))]);

        (await Create().EnforceRetentionAsync(Now, CancellationToken.None)).Should().Be(0);
    }

    [Theory]
    [InlineData("LearningEntry")]
    [InlineData("LearningIndex")]
    public async Task Learnings_KeepTheirOwnLifecycle(string type)
    {
        await _store.AddNodesAsync([Node("learning", type, owner: "u1", created: Now.AddDays(-2000))]);

        (await Create().EnforceRetentionAsync(Now, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task ATypeWithItsOwnPolicy_IsGovernedByThatPolicyNotThePersonalPeriod()
    {
        // A listed type's period is stamped as its expiry at write time; with none stamped, the personal rule must not
        // take it over.
        _config.AI.Rag.GraphRag.RetentionPolicies["Fact"] = TimeSpan.FromDays(3650);
        await _store.AddNodesAsync([Node("fact", "Fact", owner: "u1", created: Now.AddDays(-2000))]);

        (await Create().EnforceRetentionAsync(Now, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task APersonalPeriodOfZero_KeepsPersonalKnowledge()
    {
        _config.AI.Rag.GraphRag.PersonalKnowledgeRetention = TimeSpan.Zero;
        await _store.AddNodesAsync([Node("old-memory", "Preference", owner: "u1", created: Now.AddDays(-5000))]);

        (await Create().EnforceRetentionAsync(Now, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task OneRun_RemovesAtMostTheBatch()
    {
        await _store.AddNodesAsync(Enumerable.Range(0, RetentionEnforcementService.BatchSize + 5)
            .Select(i => Node($"m{i}", "Preference", owner: "u1", created: Now.AddDays(-800)))
            .ToList());

        await Create().EnforceRetentionAsync(Now, CancellationToken.None);

        _expired.Should().HaveCount(RetentionEnforcementService.BatchSize);
    }

    [Fact]
    public async Task TheRun_HappensOnTheConfiguredInterval_NotAFixedDay()
    {
        _config.AI.Rag.GraphRag.RetentionEnforcementInterval = TimeSpan.FromHours(2);
        var time = new ObservableClock(Now);
        await _store.AddNodesAsync([Node("old-memory", "Preference", owner: "u1", created: Now.AddDays(-800))]);
        using var service = Create(time);

        var armed = time.NextTimer;
        await service.StartAsync(CancellationToken.None);
        await armed.WaitAsync(Patience);
        await AdvanceAndSettleAsync(time, TimeSpan.FromSeconds(30));
        ExpiredIds.Should().Equal("old-memory");

        _expired = null;
        await _store.AddNodesAsync([Node("older-memory", "Preference", owner: "u1", created: Now.AddDays(-900))]);
        time.Advance(TimeSpan.FromHours(2) - TimeSpan.FromMinutes(1));
        _expired.Should().BeNull("control: the next run is not due yet");
        await AdvanceAndSettleAsync(time, TimeSpan.FromMinutes(1));

        ExpiredIds.Should().Contain("older-memory", "the second run comes two hours later, not twenty-four");
        await service.StopAsync(CancellationToken.None);
    }

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>Advances the clock and waits until the loop has armed its next wait, which means the run is done.</summary>
    private static async Task AdvanceAndSettleAsync(ObservableClock time, TimeSpan by)
    {
        var reArmed = time.NextTimer;
        time.Advance(by);
        await reArmed.WaitAsync(Patience);
    }

    /// <summary>A fake clock that signals each timer it creates, as the Infrastructure.AI sweep tests do.</summary>
    private sealed class ObservableClock(DateTimeOffset start) : Microsoft.Extensions.Time.Testing.FakeTimeProvider(start)
    {
        private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task NextTimer => Volatile.Read(ref _next).Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Exchange(ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
            return timer;
        }
    }

    private static GraphNode Node(
        string id, string type, string? owner, DateTimeOffset created, DateTimeOffset? expires = null, string[]? chunks = null) =>
        new()
        {
            Id = id, Name = id, Type = type, OwnerId = owner, CreatedAt = created, ExpiresAt = expires,
            ChunkIds = chunks ?? [],
        };

    private static ErasureReceipt Receipt(int nodes) => new()
    {
        RequestId = "r", ScopeId = "retention",
        RequestedAt = Now, CompletedAt = Now,
        NodesDeleted = nodes, EdgesDeleted = 0, FeedbackWeightsDeleted = 0, VectorEmbeddingsDeleted = 0,
    };
}

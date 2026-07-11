using Application.AI.Common.Interfaces.Bundles;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.Agents;
using Domain.AI.Bundles;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Bundles;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Bundles;

/// <summary>
/// Tests for <see cref="BundleRunExecutor"/> — the shared engine both the background dispatcher and the stream
/// endpoint call. These cover the engine's own decisions (not-found, already-claimed, claim-then-drive); the
/// end-to-end ambient-arming/pinning/failure-scrubbing behaviour is proven through the dispatcher in
/// <see cref="BundleRunBackgroundServiceTests"/>, which now runs through this same executor.
/// </summary>
public sealed class BundleRunExecutorTests : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly List<string> _tempDirs = [];

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private AppConfig Config()
    {
        var cfg = new AppConfig();
        cfg.AI.BundleExecution.HandleTtl = Ttl;
        cfg.AI.BundleExecution.RunRecordTtl = Ttl;
        return cfg;
    }

    private (BundleRunExecutor Executor, InMemoryBundleRunJobStore JobStore, InMemoryBundleHandleStore HandleStore)
        BuildSut(IMediator mediator)
    {
        var monitor = new StaticOptionsMonitor<AppConfig>(Config());
        var jobStore = new InMemoryBundleRunJobStore(monitor, _time);
        var handleStore = new InMemoryBundleHandleStore(monitor, _time, NullLogger<InMemoryBundleHandleStore>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(mediator);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var executor = new BundleRunExecutor(
            jobStore, handleStore, scopeFactory, _time, NullLogger<BundleRunExecutor>.Instance);
        return (executor, jobStore, handleStore);
    }

    private StagedBundle StageOnDisk(string bundleId)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bundle-executor-tests", bundleId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "AGENT.md"), "# agent");
        _tempDirs.Add(dir);
        return new StagedBundle
        {
            BundleId = bundleId,
            StagedRootDirectory = dir,
            Agent = new AgentDefinition { Id = "agent-" + bundleId, Name = "Agent " + bundleId }
        };
    }

    private BundleRunRecord QueuedRecord(string jobId, string handle, string agentId, bool streaming = false) => new()
    {
        JobId = jobId,
        Handle = handle,
        OwnerId = "owner-1",
        AgentName = agentId,
        UserMessages = ["hello"],
        MaxTurns = 3,
        Envelope = new CapabilityEnvelope(),
        Status = BundleRunStatus.Queued,
        Streaming = streaming,
        CreatedAt = _time.GetUtcNow()
    };

    private static Mock<IMediator> MediatorReturning(ConversationResult result)
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return mediator;
    }

    private static ConversationResult Ok() => new()
    {
        Success = true,
        Turns = [new TurnSummary { TurnNumber = 1, UserMessage = "hi", AgentResponse = "hello" }],
        FinalResponse = "hello",
        TotalToolInvocations = 1
    };

    [Fact]
    public async Task ExecuteAsync_UnknownJobId_ReturnsNotFound()
    {
        var (executor, _, _) = BuildSut(new Mock<IMediator>().Object);

        var result = await executor.ExecuteAsync("ghost", CancellationToken.None);

        result.Status.Should().Be(BundleRunExecutionStatus.NotFound);
        result.Record.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_NonQueuedRecord_ReturnsAlreadyClaimed_WithoutDriving()
    {
        var mediator = new Mock<IMediator>();
        var (executor, jobStore, handleStore) = BuildSut(mediator.Object);
        var staged = StageOnDisk("b1");
        var handle = handleStore.Register(staged, "owner-1");
        jobStore.Create(QueuedRecord("j1", handle, staged.Agent.Id) with { Status = BundleRunStatus.Running });

        var result = await executor.ExecuteAsync("j1", CancellationToken.None);

        result.Status.Should().Be(BundleRunExecutionStatus.AlreadyClaimed);
        mediator.Verify(m => m.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyClaimedByAnotherDriver_StandsDown()
    {
        var mediator = MediatorReturning(Ok());
        var (executor, jobStore, handleStore) = BuildSut(mediator.Object);
        var staged = StageOnDisk("b1");
        var handle = handleStore.Register(staged, "owner-1");
        jobStore.Create(QueuedRecord("j1", handle, staged.Agent.Id, streaming: true));

        // Another driver claims the run first (CAS win); this call must stand down and drive nothing.
        jobStore.TryBeginRun("j1", _time.GetUtcNow());

        var result = await executor.ExecuteAsync("j1", CancellationToken.None);

        result.Status.Should().Be(BundleRunExecutionStatus.AlreadyClaimed);
        mediator.Verify(m => m.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_QueuedStreamingRun_ClaimsAndDrivesToSucceeded()
    {
        var mediator = MediatorReturning(Ok());
        var (executor, jobStore, handleStore) = BuildSut(mediator.Object);
        var staged = StageOnDisk("b1");
        var handle = handleStore.Register(staged, "owner-1");
        jobStore.Create(QueuedRecord("j1", handle, staged.Agent.Id, streaming: true));

        var result = await executor.ExecuteAsync("j1", CancellationToken.None);

        result.Status.Should().Be(BundleRunExecutionStatus.Ran);
        result.Record!.Status.Should().Be(BundleRunStatus.Succeeded);
        result.Record.StartedAt.Should().NotBeNull();
        result.Record.Outcome!.ConversationSucceeded.Should().BeTrue();
        jobStore.Get("j1")!.Status.Should().Be(BundleRunStatus.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_HandleExpiredBeforeStart_MarksFailed_WithoutStartTime()
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("must not run"));
        var (executor, jobStore, handleStore) = BuildSut(mediator.Object);
        var staged = StageOnDisk("b1");
        var handle = handleStore.Register(staged, "owner-1");
        jobStore.Create(QueuedRecord("j1", handle, staged.Agent.Id, streaming: true));
        handleStore.Remove(handle); // handle gone before the stream connected

        var result = await executor.ExecuteAsync("j1", CancellationToken.None);

        result.Status.Should().Be(BundleRunExecutionStatus.Ran);
        result.Record!.Status.Should().Be(BundleRunStatus.Failed);
        result.Record.StartedAt.Should().BeNull("a run that never started must not carry a start time");
        result.Record.Error.Should().Contain("expired");
        mediator.Verify(m => m.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best-effort test cleanup */ }
        }
    }
}

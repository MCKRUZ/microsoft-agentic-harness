using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Services.Bundles;
using Application.AI.Common.Services.Governance;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.Agents;
using Domain.AI.Bundles;
using Domain.AI.Governance;
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
/// Tests for <see cref="BundleRunBackgroundService"/>: it drains the queue, re-arms the capability envelope
/// and ephemeral-agent overlay on its detached thread, drives the conversation, pins the staging directory
/// against the sweeper for the run's duration, and records the terminal outcome.
/// </summary>
public sealed class BundleRunBackgroundServiceTests : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));
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

    private StagedBundle StageOnDisk(string bundleId, out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "bundle-dispatch-tests", bundleId);
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

    private static ConversationResult SampleResult() => new()
    {
        Success = true,
        Turns = [new TurnSummary { TurnNumber = 1, UserMessage = "hi", AgentResponse = "hello" }],
        FinalResponse = "hello",
        TotalToolInvocations = 2
    };

    /// <summary>
    /// Builds the dispatcher wired to the real in-memory queue/stores and a scope factory whose IMediator is
    /// <paramref name="mediator"/>, so a test controls exactly what the conversation returns (or does).
    /// </summary>
    private (BundleRunBackgroundService Service,
             InMemoryBundleRunDispatchQueue Queue,
             InMemoryBundleRunJobStore JobStore,
             InMemoryBundleHandleStore HandleStore)
        BuildSut(IMediator mediator)
    {
        var monitor = new StaticOptionsMonitor<AppConfig>(Config());
        var queue = new InMemoryBundleRunDispatchQueue();
        var jobStore = new InMemoryBundleRunJobStore(monitor, _time);
        var handleStore = new InMemoryBundleHandleStore(monitor, _time, NullLogger<InMemoryBundleHandleStore>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(mediator);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var service = new BundleRunBackgroundService(
            queue, jobStore, handleStore, scopeFactory, _time,
            NullLogger<BundleRunBackgroundService>.Instance);

        return (service, queue, jobStore, handleStore);
    }

    private BundleRunRecord QueuedRecord(string jobId, string handle, string agentId, CapabilityEnvelope envelope) => new()
    {
        JobId = jobId,
        Handle = handle,
        AgentName = agentId,
        UserMessages = ["hello"],
        MaxTurns = 3,
        Envelope = envelope,
        Status = BundleRunStatus.Queued,
        CreatedAt = _time.GetUtcNow()
    };

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Predicate did not become true within {timeout.TotalMilliseconds}ms.");
    }

    [Fact]
    public async Task Dispatch_RunsConversationUnderArmedEnvelopeAndOverlay_MarksSucceeded()
    {
        CapabilityEnvelope? seenEnvelope = null;
        EphemeralAgentOverlay? seenOverlay = null;

        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .Returns((RunConversationCommand _, CancellationToken _) =>
            {
                // Captured INSIDE Send: proves both ambients are armed for the duration of the conversation.
                seenEnvelope = CapabilityEnvelopeAccessor.Current;
                seenOverlay = EphemeralAgentOverlayAccessor.Current;
                return Task.FromResult(SampleResult());
            });

        var (service, queue, jobStore, handleStore) = BuildSut(mediator.Object);
        var staged = StageOnDisk("b1", out _);
        var handle = handleStore.Register(staged);
        var envelope = new CapabilityEnvelope { AllowedTools = ["read_file"], AutonomyCeiling = AutonomyLevel.Autonomous };
        jobStore.Create(QueuedRecord("j1", handle, staged.Agent.Id, envelope));
        await queue.EnqueueAsync("j1", CancellationToken.None);

        _ = service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => jobStore.Get("j1")?.IsTerminal == true, TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        seenEnvelope.Should().BeSameAs(envelope);
        seenOverlay.Should().NotBeNull();
        seenOverlay!.Agent.Id.Should().Be(staged.Agent.Id);

        var record = jobStore.Get("j1")!;
        record.Status.Should().Be(BundleRunStatus.Succeeded);
        record.StartedAt.Should().NotBeNull();
        record.CompletedAt.Should().NotBeNull();
        record.Outcome.Should().NotBeNull();
        record.Outcome!.ConversationSucceeded.Should().BeTrue();
        record.Outcome.FinalResponse.Should().Be("hello");
        record.Outcome.TotalToolInvocations.Should().Be(2);

        // The ambient must NOT leak past the run.
        CapabilityEnvelopeAccessor.Current.Should().BeNull();
        EphemeralAgentOverlayAccessor.Current.Should().BeNull();
    }

    [Fact]
    public async Task Dispatch_PinsStagingDirectory_ForDurationOfRun()
    {
        var dirExistedDuringRun = false;
        var sweepDuringRun = -1;

        InMemoryBundleHandleStore? handleStoreRef = null;
        string? dirRef = null;

        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .Returns((RunConversationCommand _, CancellationToken _) =>
            {
                // While the run is executing, expire the clock and sweep: the pinned directory must survive.
                _time.Advance(Ttl + TimeSpan.FromMinutes(5));
                sweepDuringRun = handleStoreRef!.SweepExpired();
                dirExistedDuringRun = Directory.Exists(dirRef!);
                return Task.FromResult(SampleResult());
            });

        var (service, queue, jobStore, handleStore) = BuildSut(mediator.Object);
        handleStoreRef = handleStore;
        var staged = StageOnDisk("b1", out var dir);
        dirRef = dir;
        var handle = handleStore.Register(staged);
        jobStore.Create(QueuedRecord("j1", handle, staged.Agent.Id, new CapabilityEnvelope()));
        await queue.EnqueueAsync("j1", CancellationToken.None);

        _ = service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => jobStore.Get("j1")?.IsTerminal == true, TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        sweepDuringRun.Should().Be(0, "a leased handle is pinned against the sweeper");
        dirExistedDuringRun.Should().BeTrue("the staging directory must survive while the run reads its skills");

        // Releasing the lease slides the TTL forward (a completed run is a use), so the handle survives the
        // immediate sweep; advance past the fresh TTL and it becomes reclaimable and its directory is deleted.
        handleStore.SweepExpired().Should().Be(0);
        Directory.Exists(dir).Should().BeTrue();
        _time.Advance(Ttl + TimeSpan.FromMinutes(1));
        handleStore.SweepExpired().Should().Be(1);
        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public async Task Dispatch_HandleExpiredBeforePickup_MarksFailed()
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("should not run"));

        var (service, queue, jobStore, handleStore) = BuildSut(mediator.Object);
        var staged = StageOnDisk("b1", out _);
        var handle = handleStore.Register(staged);
        jobStore.Create(QueuedRecord("j1", handle, staged.Agent.Id, new CapabilityEnvelope()));
        // Remove the handle before the run is dispatched.
        handleStore.Remove(handle);
        await queue.EnqueueAsync("j1", CancellationToken.None);

        _ = service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => jobStore.Get("j1")?.IsTerminal == true, TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        var record = jobStore.Get("j1")!;
        record.Status.Should().Be(BundleRunStatus.Failed);
        record.Error.Should().Contain("expired");
        record.StartedAt.Should().BeNull("a run that never started must not carry a start time");
        mediator.Verify(m => m.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Dispatch_ConversationThrows_MarksFailed_WithScrubbedReason()
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("secret internal detail"));

        var (service, queue, jobStore, handleStore) = BuildSut(mediator.Object);
        var staged = StageOnDisk("b1", out _);
        var handle = handleStore.Register(staged);
        jobStore.Create(QueuedRecord("j1", handle, staged.Agent.Id, new CapabilityEnvelope()));
        await queue.EnqueueAsync("j1", CancellationToken.None);

        _ = service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => jobStore.Get("j1")?.IsTerminal == true, TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        var record = jobStore.Get("j1")!;
        record.Status.Should().Be(BundleRunStatus.Failed);
        record.Error.Should().Be("bundle_run.unhandled_exception");
        record.Error.Should().NotContain("secret internal detail");
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

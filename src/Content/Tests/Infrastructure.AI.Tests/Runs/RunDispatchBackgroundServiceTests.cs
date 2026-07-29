using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Bundles;
using Domain.AI.Runs;
using Domain.Common;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// Tests for <see cref="RunDispatchBackgroundService"/>.
/// </summary>
/// <remarks>
/// The property that matters most is that a claimed run always reaches a terminal state. Once a run
/// is claimed it is <see cref="RunStatus.Running"/>, and only the dispatcher can move it out of that
/// state — so any path that escapes without recording an outcome leaves a caller polling a run
/// nothing will ever finish.
/// </remarks>
public sealed class RunDispatchBackgroundServiceTests
{
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class StubExecutor(Func<RunRecord, Task<Result>> behaviour) : IRunKindExecutor
    {
        public int Invocations { get; private set; }

        public Task<Result> ExecuteAsync(RunRecord record, CancellationToken cancellationToken)
        {
            Invocations++;
            return behaviour(record);
        }
    }

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly AppConfig _config = new();

    private (RunDispatchBackgroundService Service, IRunJobStore Store, IRunDispatchQueue Queue) Build(
        IRunKindExecutor? executor)
    {
        var services = new ServiceCollection();
        if (executor is not null)
            services.AddKeyedSingleton(RunKind.Workflow, executor);

        var store = new InMemoryRunJobStore(new StaticOptionsMonitor<AppConfig>(_config), _time);
        var queue = new InMemoryRunDispatchQueue();

        var service = new RunDispatchBackgroundService(
            queue, store, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _time, NullLogger<RunDispatchBackgroundService>.Instance);

        return (service, store, queue);
    }

    private RunRecord Queued(string jobId = "job-1") => new()
    {
        JobId = jobId,
        Kind = RunKind.Workflow,
        TargetId = Guid.NewGuid().ToString(),
        OwnerId = "alice",
        Envelope = new CapabilityEnvelope(),
        Status = RunStatus.Queued,
        CreatedAt = _time.GetUtcNow()
    };

    /// <summary>Runs the dispatcher until <paramref name="jobId"/> is terminal, or the attempt budget is spent.</summary>
    private static async Task<RunRecord?> DrainUntilTerminalAsync(
        RunDispatchBackgroundService service, IRunJobStore store, string jobId)
    {
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        RunRecord? record = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            record = store.Get(jobId, "alice");
            if (record?.IsTerminal == true)
                break;

            await Task.Delay(10);
        }

        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);
        return record;
    }

    [Fact]
    public async Task ASuccessfulRun_IsRecordedSucceeded()
    {
        var executor = new StubExecutor(_ => Task.FromResult(Result.Success()));
        var (service, store, queue) = Build(executor);
        store.Create(Queued());
        await queue.EnqueueAsync("job-1", CancellationToken.None);

        var record = await DrainUntilTerminalAsync(service, store, "job-1");

        record!.Status.Should().Be(RunStatus.Succeeded);
        record.CompletedAt.Should().NotBeNull();
        executor.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task AFailedRun_KeepsTheExecutorsReason()
    {
        // The executor's messages are caller-safe by contract, so they reach the caller rather than
        // being flattened into a uniform "it failed" that says nothing actionable.
        var executor = new StubExecutor(_ => Task.FromResult(Result.Fail("the model deployment was rejected")));
        var (service, store, queue) = Build(executor);
        store.Create(Queued());
        await queue.EnqueueAsync("job-1", CancellationToken.None);

        var record = await DrainUntilTerminalAsync(service, store, "job-1");

        record!.Status.Should().Be(RunStatus.Failed);
        record.Error.Should().Contain("the model deployment was rejected");
    }

    [Fact]
    public async Task AnExecutorThatThrows_StillLeavesTheRunTerminal()
    {
        // The stranding case. Without a guard around the claimed run, this leaves it Running forever
        // and a caller polls a job nothing will ever finish.
        var executor = new StubExecutor(_ => throw new InvalidOperationException("boom"));
        var (service, store, queue) = Build(executor);
        store.Create(Queued());
        await queue.EnqueueAsync("job-1", CancellationToken.None);

        var record = await DrainUntilTerminalAsync(service, store, "job-1");

        record!.Status.Should().Be(RunStatus.Failed);
        record.Error.Should().NotContain("boom", "the caller gets a stable reason, not exception text");
    }

    [Fact]
    public async Task AKindWithNoRegisteredExecutor_FailsThatRunRatherThanTheDispatcher()
    {
        // A wiring gap must not take the loop down: that would turn one mis-registration into an
        // outage for every other kind of work in the host.
        var (service, store, queue) = Build(executor: null);
        store.Create(Queued("orphan"));
        store.Create(Queued("job-1"));
        await queue.EnqueueAsync("orphan", CancellationToken.None);
        await queue.EnqueueAsync("job-1", CancellationToken.None);

        var orphan = await DrainUntilTerminalAsync(service, store, "orphan");

        orphan!.Status.Should().Be(RunStatus.Failed);

        // The reason must name the wiring gap. Letting this fall through to the generic
        // "failed unexpectedly" guard would tell an operator the run broke, when in fact the host was
        // never configured to execute that kind of work — a different problem with a different fix.
        orphan.Error.Should().Contain("cannot execute that kind of run");

        store.Get("job-1", "alice")!.IsTerminal.Should().BeTrue(
            "the dispatcher must keep draining after a run it cannot execute");
    }

    [Fact]
    public async Task ARunAlreadyClaimed_IsSkippedRatherThanRunTwice()
    {
        // Redelivery is harmless precisely because the claim is what gates execution.
        var executor = new StubExecutor(_ => Task.FromResult(Result.Success()));
        var (service, store, queue) = Build(executor);
        store.Create(Queued());
        store.TryBeginRun("job-1", _time.GetUtcNow());

        await queue.EnqueueAsync("job-1", CancellationToken.None);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(150);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        executor.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task AnUnknownJobId_IsSkippedWithoutStoppingTheLoop()
    {
        var executor = new StubExecutor(_ => Task.FromResult(Result.Success()));
        var (service, store, queue) = Build(executor);
        store.Create(Queued("real"));

        await queue.EnqueueAsync("never-existed", CancellationToken.None);
        await queue.EnqueueAsync("real", CancellationToken.None);

        var record = await DrainUntilTerminalAsync(service, store, "real");

        record!.Status.Should().Be(RunStatus.Succeeded);
    }
}

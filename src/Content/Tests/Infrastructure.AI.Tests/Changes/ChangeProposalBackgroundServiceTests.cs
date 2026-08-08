using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Tests.Changes;

/// <summary>
/// Tests for <see cref="ChangeProposalBackgroundService"/>: drains the dispatch
/// queue and drives <see cref="IChangeProposalOrchestrator.ProcessAsync"/> per
/// proposal id, creating a per-dispatch scope, swallowing failures so one bad
/// proposal can't stall the queue, and honouring host shutdown.
/// </summary>
public sealed class ChangeProposalBackgroundServiceTests
{
    private sealed class RecordingOrchestrator : IChangeProposalOrchestrator
    {
        public List<string> Calls { get; } = new();
        public List<OrchestratorMode> Modes { get; } = new();
        public Func<string, ChangeProposal?>? ResultFor { get; set; }
        public Func<string, Exception>? ThrowFor { get; set; }

        public Task<ChangeProposal?> ProcessAsync(string proposalId, OrchestratorMode mode, CancellationToken ct)
        {
            Calls.Add(proposalId);
            Modes.Add(mode);
            var ex = ThrowFor?.Invoke(proposalId);
            if (ex is not null) throw ex;
            return Task.FromResult(ResultFor?.Invoke(proposalId));
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static IOptionsMonitor<AppConfig> Monitor(string mode = "Live")
    {
        var cfg = new AppConfig();
        cfg.AI.Changes.DefaultMode = mode;
        return new StaticOptionsMonitor<AppConfig>(cfg);
    }

    private static (ChangeProposalBackgroundService Service,
                    InMemoryChangeProposalDispatchQueue Queue,
                    RecordingOrchestrator Orchestrator)
        BuildSut(IOptionsMonitor<AppConfig>? config = null)
    {
        var queue = new InMemoryChangeProposalDispatchQueue();
        var orchestrator = new RecordingOrchestrator();
        var services = new ServiceCollection();
        services.AddSingleton<IChangeProposalOrchestrator>(orchestrator);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var service = new ChangeProposalBackgroundService(
            queue,
            scopeFactory,
            config ?? Monitor(),
            NullLogger<ChangeProposalBackgroundService>.Instance);
        return (service, queue, orchestrator);
    }

    [Theory]
    [InlineData("99")]                  // outside the defined range
    [InlineData(" 99")]                 // and behind a stray space
    [InlineData("Shadow,Live")]         // comma-composite — notably NOT equal to Shadow
    [InlineData("Nonsense")]
    public async Task ExecuteAsync_NonNameDefaultMode_FallsBackToShadow(string configured)
    {
        // #300, and the sharpest consequence in the whole sweep. MergeGate short-circuits its real
        // apply on `Mode == Shadow`, so ANY value that is not exactly Shadow performs a production
        // write. A bare Enum.TryParse accepted "99" and "Shadow,Live" — neither of which equals
        // Shadow — so a config typo silently converted a dry run into real applies, while the
        // documented fallback for an unparseable value is Shadow precisely to prevent that.
        var (service, queue, orchestrator) = BuildSut(Monitor(configured));
        await queue.EnqueueAsync("p1", CancellationToken.None);

        await service.StartAsync(CancellationToken.None);
        await WaitForCallsAsync(orchestrator, 1);
        await service.StopAsync(CancellationToken.None);

        orchestrator.Modes.Should().ContainSingle().Which.Should().Be(OrchestratorMode.Shadow);
    }

    [Fact]
    public async Task ExecuteAsync_NamedDefaultMode_IsStillHonoured()
    {
        // The control: refusing non-names must not mean pinning everything to Shadow. A real apply
        // must still be reachable when the operator names Live.
        var (service, queue, orchestrator) = BuildSut(Monitor(nameof(OrchestratorMode.Live)));
        await queue.EnqueueAsync("p1", CancellationToken.None);

        await service.StartAsync(CancellationToken.None);
        await WaitForCallsAsync(orchestrator, 1);
        await service.StopAsync(CancellationToken.None);

        orchestrator.Modes.Should().ContainSingle().Which.Should().Be(OrchestratorMode.Live);
    }

    private static async Task WaitForCallsAsync(RecordingOrchestrator orchestrator, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (orchestrator.Calls.Count < count && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    [Fact]
    public async Task ExecuteAsync_DrainsQueueInOrderAndCallsOrchestratorForEach()
    {
        var (service, queue, orchestrator) = BuildSut();
        await queue.EnqueueAsync("p1", CancellationToken.None);
        await queue.EnqueueAsync("p2", CancellationToken.None);
        await queue.EnqueueAsync("p3", CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);
        // Poll until the orchestrator has seen all three; then stop the
        // service. Bound the wait so a hang doesn't deadlock the test.
        await WaitForAsync(() => orchestrator.Calls.Count == 3, TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);
        cts.Cancel();
        try { await run; } catch { /* StartAsync returns when ExecuteAsync starts; nothing to await */ }

        orchestrator.Calls.Should().Equal("p1", "p2", "p3");
    }

    [Fact]
    public async Task ExecuteAsync_OrchestratorThrows_LogsAndContinuesDraining()
    {
        var (service, queue, orchestrator) = BuildSut();
        orchestrator.ThrowFor = id => id == "boom" ? new InvalidOperationException("simulated") : null!;

        await queue.EnqueueAsync("p1", CancellationToken.None);
        await queue.EnqueueAsync("boom", CancellationToken.None);
        await queue.EnqueueAsync("p3", CancellationToken.None);

        using var cts = new CancellationTokenSource();
        _ = service.StartAsync(cts.Token);
        await WaitForAsync(() => orchestrator.Calls.Count == 3, TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        // All three were attempted; the throw on p2 didn't kill the loop.
        orchestrator.Calls.Should().Equal("p1", "boom", "p3");
    }

    [Fact]
    public async Task ExecuteAsync_HonoursShutdownToken_StopsDraining()
    {
        var (service, queue, orchestrator) = BuildSut();
        await queue.EnqueueAsync("p1", CancellationToken.None);
        await queue.EnqueueAsync("p2", CancellationToken.None);

        using var cts = new CancellationTokenSource();
        _ = service.StartAsync(cts.Token);
        await WaitForAsync(() => orchestrator.Calls.Count >= 1, TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);
        cts.Cancel();

        // p1 should have been picked up before shutdown; we don't assert
        // anything about p2 — depending on timing it may or may not have
        // been dispatched before StopAsync took effect. The point is the
        // service doesn't deadlock on shutdown.
        orchestrator.Calls.Should().Contain("p1");
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException(
            $"Predicate did not become true within {timeout.TotalMilliseconds}ms.");
    }
}

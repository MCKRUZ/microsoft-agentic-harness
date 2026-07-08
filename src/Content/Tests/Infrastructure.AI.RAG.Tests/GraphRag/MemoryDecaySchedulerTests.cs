using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.RAG;
using Infrastructure.AI.RAG.GraphRag;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.GraphRag;

/// <summary>
/// Tests for <see cref="MemoryDecayScheduler"/> proving the hosted service actually drives
/// <see cref="IMemoryDecayService"/> on its live tick path — not just in unit-level calls.
/// </summary>
public sealed class MemoryDecaySchedulerTests
{
    [Fact]
    public async Task Scheduler_OnTick_InvokesApplyDecayAndPruneFromScope()
    {
        // Arrange — spy decay service that signals the first time ApplyDecayAsync fires.
        var decayFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyCount = 0;
        var spy = new Mock<IMemoryDecayService>();
        spy.Setup(s => s.ApplyDecayAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref applyCount);
                decayFired.TrySetResult();
                return Task.CompletedTask;
            });
        spy.Setup(s => s.PruneAsync(It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var config = BuildConfig(enabled: true, interval: TimeSpan.FromMilliseconds(20), pruneThreshold: 0.05);
        var scopeFactory = BuildScopeFactory(spy.Object);

        var scheduler = new MemoryDecayScheduler(
            scopeFactory,
            config,
            NullLogger<MemoryDecayScheduler>.Instance);

        // Act — start the hosted service and wait for the first tick to drive decay.
        await scheduler.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(decayFired.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        await scheduler.StopAsync(CancellationToken.None);

        // Assert — decay ran on the scheduler's live path, resolved from a fresh scope.
        Assert.Same(decayFired.Task, completed);
        Assert.True(applyCount >= 1, "Expected ApplyDecayAsync to be invoked at least once by the scheduler tick.");
        spy.Verify(s => s.PruneAsync(0.05, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    private static IServiceScopeFactory BuildScopeFactory(IMemoryDecayService decayService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(decayService);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private static IOptionsMonitor<AppConfig> BuildConfig(bool enabled, TimeSpan interval, double pruneThreshold)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Rag = new RagConfig
                {
                    CrossSessionMemory = new CrossSessionMemoryConfig
                    {
                        PruneThreshold = pruneThreshold,
                        DecayScheduler = new MemoryDecaySchedulerConfig
                        {
                            Enabled = enabled,
                            Interval = interval
                        }
                    }
                }
            }
        };

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(appConfig);
        return monitor.Object;
    }
}

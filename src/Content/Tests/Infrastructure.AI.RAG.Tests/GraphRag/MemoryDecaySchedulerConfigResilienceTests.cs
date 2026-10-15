using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.AI.RAG.GraphRag;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.GraphRag;

/// <summary>
/// Pins that a hot-reloaded configuration value that fails validation cannot stop the host through the
/// decay scheduler (#821): the prune threshold is read on every pass, and an uncontained throw there
/// escapes <c>ExecuteAsync</c>, which stops the host.
/// </summary>
public sealed class MemoryDecaySchedulerConfigResilienceTests
{
    [Fact]
    public async Task Scheduler_ConfigBecomesUnreadableBetweenPasses_KeepsRunningOnTheLastGoodThreshold()
    {
        var spy = new Mock<IMemoryDecayService>();
        spy.Setup(s => s.ApplyDecayAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        spy.Setup(s => s.PruneAsync(It.IsAny<double>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var monitor = new SwitchableMonitor(new AppConfig
        {
            AI = new AIConfig
            {
                Rag = new RagConfig
                {
                    CrossSessionMemory = new CrossSessionMemoryConfig
                    {
                        PruneThreshold = 0.05,
                        DecayScheduler = new MemoryDecaySchedulerConfig
                        {
                            Enabled = true,
                            Interval = TimeSpan.FromMilliseconds(20),
                        },
                    },
                },
            },
        });
        var services = new ServiceCollection().AddSingleton(spy.Object).BuildServiceProvider();
        var sut = new MemoryDecayScheduler(
            services.GetRequiredService<IServiceScopeFactory>(), monitor, NullLogger<MemoryDecayScheduler>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(150);

        // The bad edit lands while the scheduler is ticking; the per-pass read is the one that escaped.
        monitor.Throwing = true;
        await Task.Delay(300);

        sut.ExecuteTask!.IsCompleted.Should().BeFalse("an unreadable threshold must not end the scheduler");
        spy.Verify(s => s.PruneAsync(0.05, It.IsAny<CancellationToken>()), Times.AtLeast(2));
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Scheduler_ConfigUnreadableWhenItStarts_KeepsRunning()
    {
        var services = new ServiceCollection().AddSingleton(Mock.Of<IMemoryDecayService>()).BuildServiceProvider();
        var monitor = new SwitchableMonitor(new AppConfig()) { Throwing = true };
        var sut = new MemoryDecayScheduler(
            services.GetRequiredService<IServiceScopeFactory>(), monitor, NullLogger<MemoryDecayScheduler>.Instance);

        // The interval is read once, before ExecuteAsync's first await, so an unguarded read there
        // faults StartAsync itself.
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(150);

        sut.ExecuteTask!.IsCompleted.Should().BeFalse("an unreadable interval must not end the scheduler");
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Scheduler_ThresholdNeverRead_PrunesNothingOnTheFirstPass()
    {
        // Read order: 1 interval prime, 2 threshold prime, 3 interval at ExecuteAsync start, 4 threshold at the
        // first pass. Failing 2 and 4 leaves the threshold with no good value when the first prune asks for it,
        // while the interval (read 3) stays readable so passes run on the test's 20ms cadence.
        var thresholds = new List<double>();
        var spy = new Mock<IMemoryDecayService>();
        spy.Setup(s => s.ApplyDecayAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        spy.Setup(s => s.PruneAsync(It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .Callback<double, CancellationToken>((threshold, _) => thresholds.Add(threshold))
            .Returns(Task.CompletedTask);
        var config = new AppConfig();
        config.AI.Rag.CrossSessionMemory.PruneThreshold = 0.05;
        config.AI.Rag.CrossSessionMemory.DecayScheduler.Interval = TimeSpan.FromMilliseconds(20);
        var services = new ServiceCollection().AddSingleton(spy.Object).BuildServiceProvider();
        var sut = new MemoryDecayScheduler(
            services.GetRequiredService<IServiceScopeFactory>(), new ScriptedOptionsMonitor(config, 2, 4),
            NullLogger<MemoryDecayScheduler>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await sut.StopAsync(CancellationToken.None);

        thresholds.Should().NotBeEmpty();
        thresholds[0].Should().Be(double.NegativeInfinity,
            "a threshold that was never read must prune nothing, not fall back to the shipped default");
    }

    private sealed class SwitchableMonitor(AppConfig value) : IOptionsMonitor<AppConfig>
    {
        public volatile bool Throwing;

        public AppConfig CurrentValue => Throwing
            ? throw new OptionsValidationException("AppConfig", typeof(AppConfig), ["a mistyped live edit"])
            : value;

        public AppConfig Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}

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

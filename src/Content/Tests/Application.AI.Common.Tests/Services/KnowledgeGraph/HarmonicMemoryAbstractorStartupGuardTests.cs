using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI.HarmonicMemory;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.KnowledgeGraph;

/// <summary>
/// Reproduces #597: enabling <c>AppConfig:AI:HarmonicMemory:Mode</c> above <c>Off</c> without
/// registering a real <see cref="IMemoryAbstractor"/> should fail at host startup, not silently
/// on the first <c>RememberAsync</c> call inside a live conversation turn.
/// </summary>
public sealed class HarmonicMemoryAbstractorStartupGuardTests
{
    [Fact]
    public async Task StartAsync_ModeOffWithNotConfiguredAbstractor_DoesNotThrow()
    {
        var sut = CreateGuard(HarmonicMemoryMode.Off, new NotConfiguredMemoryAbstractor());

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(HarmonicMemoryMode.AbstractOnly)]
    [InlineData(HarmonicMemoryMode.Full)]
    public async Task StartAsync_ModeEnabledWithNotConfiguredAbstractor_ThrowsAtStartupInsteadOfFirstWrite(
        HarmonicMemoryMode mode)
    {
        var sut = CreateGuard(mode, new NotConfiguredMemoryAbstractor());

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IMemoryAbstractor*");
    }

    [Fact]
    public async Task StartAsync_ModeEnabledWithRealAbstractor_DoesNotThrow()
    {
        var sut = CreateGuard(HarmonicMemoryMode.Full, new FakeAbstractor());

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private static HarmonicMemoryAbstractorStartupGuard CreateGuard(HarmonicMemoryMode mode, IMemoryAbstractor abstractor)
    {
        var appConfig = new AppConfig();
        appConfig.AI.HarmonicMemory.Mode = mode;

        var optionsMonitor = new Mock<IOptionsMonitor<AppConfig>>();
        optionsMonitor.SetupGet(x => x.CurrentValue).Returns(appConfig);

        return new HarmonicMemoryAbstractorStartupGuard(optionsMonitor.Object, abstractor);
    }

    private sealed class FakeAbstractor : IMemoryAbstractor
    {
        public Task<MemoryAbstraction> AbstractAsync(string content, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryAbstraction { Abstraction = content });
    }
}

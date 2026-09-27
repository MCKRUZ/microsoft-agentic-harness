using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI.HarmonicMemory;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.KnowledgeGraph;

/// <summary>
/// Reproduces #597: enabling <c>AppConfig:AI:HarmonicMemory:Mode</c> above <c>Off</c> without
/// registering a real <see cref="IMemoryAbstractor"/> (or, in <see cref="HarmonicMemoryMode.Full"/>, a
/// real <see cref="IMemoryConsolidator"/>) should fail at host startup, not silently on the first
/// <c>RememberAsync</c> call inside a live conversation turn.
/// </summary>
public sealed class HarmonicMemoryAbstractorStartupGuardTests
{
    [Fact]
    public async Task StartAsync_ModeOffWithNotConfiguredAbstractor_DoesNotThrow()
    {
        var sut = CreateGuard(HarmonicMemoryMode.Off, s => s.AddSingleton<IMemoryAbstractor, NotConfiguredMemoryAbstractor>());

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(HarmonicMemoryMode.AbstractOnly)]
    [InlineData(HarmonicMemoryMode.Full)]
    public async Task StartAsync_ModeEnabledWithNotConfiguredAbstractor_ThrowsAtStartupInsteadOfFirstWrite(
        HarmonicMemoryMode mode)
    {
        var sut = CreateGuard(mode, s => s.AddSingleton<IMemoryAbstractor, NotConfiguredMemoryAbstractor>());

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IMemoryAbstractor*");
    }

    [Fact]
    public async Task StartAsync_ModeEnabledWithRealAbstractor_DoesNotThrow()
    {
        var sut = CreateGuard(HarmonicMemoryMode.AbstractOnly, s => s.AddSingleton<IMemoryAbstractor, FakeAbstractor>());

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_ScopedAbstractorRegistration_DoesNotThrowCaptiveDependencyError()
    {
        // Code-review finding: this guard is itself a singleton, and a consumer's agent-backed
        // IMemoryAbstractor is free to register at Scoped lifetime (plausible for one that needs
        // per-conversation state) — HarmonicMemoryDependencyInjection's own doc comment invites this.
        // Ctor-injecting IMemoryAbstractor directly would fail ValidateScopes on every host boot
        // regardless of Mode, replacing this guard's clear error with an opaque DI crash. Built with
        // ValidateScopes: true so a regression back to ctor-injection fails this test, not just at
        // runtime in a real host.
        var sut = CreateGuard(HarmonicMemoryMode.AbstractOnly, s => s.AddScoped<IMemoryAbstractor, FakeAbstractor>());

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_ModeFullWithNotConfiguredConsolidator_ThrowsAtStartup()
    {
        // Simplify-review altitude finding: the guard originally checked only IMemoryAbstractor, but
        // NotConfiguredMemoryConsolidator has the identical failure shape and is reached whenever Full
        // mode finds a similar existing entry — the same "fixed one instance, left the sibling" mistake
        // this repo's own CLAUDE.md already records twice.
        var sut = CreateGuard(HarmonicMemoryMode.Full, s =>
        {
            s.AddSingleton<IMemoryAbstractor, FakeAbstractor>();
            s.AddSingleton<IMemoryConsolidator, NotConfiguredMemoryConsolidator>();
        });

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IMemoryConsolidator*");
    }

    [Fact]
    public async Task StartAsync_ModeAbstractOnlyWithNotConfiguredConsolidator_DoesNotThrow()
    {
        // The consolidator is unreached in AbstractOnly (it never consolidates), so an unconfigured
        // consolidator must not block startup in that mode.
        var sut = CreateGuard(HarmonicMemoryMode.AbstractOnly, s =>
        {
            s.AddSingleton<IMemoryAbstractor, FakeAbstractor>();
            s.AddSingleton<IMemoryConsolidator, NotConfiguredMemoryConsolidator>();
        });

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_ModeFullWithRealConsolidator_DoesNotThrow()
    {
        var sut = CreateGuard(HarmonicMemoryMode.Full, s =>
        {
            s.AddSingleton<IMemoryAbstractor, FakeAbstractor>();
            s.AddSingleton<IMemoryConsolidator, FakeConsolidator>();
        });

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_ScopedConsolidatorRegistration_DoesNotThrowCaptiveDependencyError()
    {
        var sut = CreateGuard(HarmonicMemoryMode.Full, s =>
        {
            s.AddSingleton<IMemoryAbstractor, FakeAbstractor>();
            s.AddScoped<IMemoryConsolidator, FakeConsolidator>();
        });

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private static HarmonicMemoryAbstractorStartupGuard CreateGuard(
        HarmonicMemoryMode mode, Action<IServiceCollection> registerServices)
    {
        var appConfig = new AppConfig();
        appConfig.AI.HarmonicMemory.Mode = mode;

        var optionsMonitor = new Mock<IOptionsMonitor<AppConfig>>();
        optionsMonitor.SetupGet(x => x.CurrentValue).Returns(appConfig);

        var services = new ServiceCollection();
        registerServices(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        return new HarmonicMemoryAbstractorStartupGuard(
            optionsMonitor.Object, provider.GetRequiredService<IServiceScopeFactory>());
    }

    private sealed class FakeAbstractor : IMemoryAbstractor
    {
        public Task<MemoryAbstraction> AbstractAsync(string content, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryAbstraction { Abstraction = content });
    }

    private sealed class FakeConsolidator : IMemoryConsolidator
    {
        public Task<MemoryConsolidationDecision> ConsolidateAsync(
            MemoryAbstraction candidate,
            string candidateValue,
            IReadOnlyList<ExistingMemory> similarExisting,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MemoryConsolidationDecision.Create());
    }
}

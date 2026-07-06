using Application.AI.Common.Extensions;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Application.AI.Common.Tests.Extensions;

public sealed class HarmonicMemoryDependencyInjectionTests
{
    [Fact]
    public void AddHarmonicMemoryDependencies_RegistersNotConfiguredDefaults()
    {
        using var provider = new ServiceCollection()
            .AddHarmonicMemoryDependencies()
            .BuildServiceProvider();

        provider.GetRequiredService<IMemoryAbstractor>().Should().BeOfType<NotConfiguredMemoryAbstractor>();
        provider.GetRequiredService<IMemoryConsolidator>().Should().BeOfType<NotConfiguredMemoryConsolidator>();
    }

    [Fact]
    public void AddHarmonicMemoryDependencies_PreservesPreRegisteredRealImplementations()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMemoryAbstractor, FakeAbstractor>();

        using var provider = services.AddHarmonicMemoryDependencies().BuildServiceProvider();

        // TryAddSingleton must not clobber a real impl registered before the call.
        provider.GetRequiredService<IMemoryAbstractor>().Should().BeOfType<FakeAbstractor>();
        provider.GetRequiredService<IMemoryConsolidator>().Should().BeOfType<NotConfiguredMemoryConsolidator>();
    }

    private sealed class FakeAbstractor : IMemoryAbstractor
    {
        public Task<MemoryAbstraction> AbstractAsync(string content, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryAbstraction { Abstraction = content });
    }
}

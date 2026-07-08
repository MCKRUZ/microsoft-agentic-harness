using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI.HarmonicMemory;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Memory;

/// <summary>
/// Regression tests that exercise the <em>real</em> dependency-injection path for
/// <see cref="IKnowledgeMemory"/> — building an actual <see cref="ServiceProvider"/> from
/// <see cref="DependencyInjection.AddKnowledgeGraphDependencies"/> and resolving the service — rather than
/// hand-constructing <c>KnowledgeMemoryService</c> as the other harmonic tests do.
/// </summary>
/// <remarks>
/// These cover the bug where the <c>IKnowledgeMemory</c> factory never resolved
/// <see cref="IMemoryAbstractor"/> / <see cref="IMemoryConsolidator"/> from the container, so both were
/// always <see langword="null"/> in a DI host: raising <c>HarmonicMemory:Mode</c> then threw on the write
/// path (AbstractOnly) or silently degraded Full mode to AbstractOnly. The tests register recording test
/// doubles and assert they are actually invoked through the resolved service, which fails when the factory
/// drops them on the floor and passes once they are wired.
/// </remarks>
public sealed class KnowledgeMemoryServiceDiWiringTests
{
    private const string LongEnoughContent = "content long enough to reach the harmonic write path";

    [Fact]
    public async Task AbstractOnly_ResolvedFromDi_InvokesAbstractor_DoesNotThrow()
    {
        var abstractor = new RecordingAbstractor();
        var consolidator = new RecordingConsolidator();
        await using var provider = BuildProvider(HarmonicMemoryMode.AbstractOnly, abstractor, consolidator);

        using var scope = provider.CreateScope();
        var memory = scope.ServiceProvider.GetRequiredService<IKnowledgeMemory>();

        var act = () => memory.RememberAsync("azure", LongEnoughContent);

        await act.Should().NotThrowAsync(
            "the DI factory must resolve the registered IMemoryAbstractor instead of passing null");
        abstractor.Calls.Should().Be(1, "AbstractOnly reaches the wired abstractor through the real DI path");
        consolidator.Calls.Should().Be(0, "AbstractOnly never consolidates");
    }

    [Fact]
    public async Task Full_ResolvedFromDi_InvokesConsolidator_WhenSimilarEntryExists()
    {
        var abstractor = new RecordingAbstractor
        {
            Result = new MemoryAbstraction { Abstraction = "shared cluster topic" }
        };
        var consolidator = new RecordingConsolidator();
        await using var provider = BuildProvider(HarmonicMemoryMode.Full, abstractor, consolidator);

        using var scope = provider.CreateScope();
        var memory = scope.ServiceProvider.GetRequiredService<IKnowledgeMemory>();

        // First write seeds a trusted, abstracted candidate; the second shares its abstraction tokens so
        // consolidation candidates are found and the consolidator is consulted. If the factory left the
        // consolidator null, Full would silently degrade to AbstractOnly and Calls would stay 0.
        await memory.RememberAsync("fact-one", LongEnoughContent + " one");
        await memory.RememberAsync("fact-two", LongEnoughContent + " two");

        abstractor.Calls.Should().Be(2, "Full mode abstracts every qualifying fact");
        consolidator.Calls.Should().BeGreaterThanOrEqualTo(1,
            "the DI factory must resolve the registered IMemoryConsolidator so Full mode actually consolidates");
    }

    [Fact]
    public async Task Off_ResolvedFromDi_DoesNotInvokeAbstractor()
    {
        var abstractor = new RecordingAbstractor();
        var consolidator = new RecordingConsolidator();
        await using var provider = BuildProvider(HarmonicMemoryMode.Off, abstractor, consolidator);

        using var scope = provider.CreateScope();
        var memory = scope.ServiceProvider.GetRequiredService<IKnowledgeMemory>();

        await memory.RememberAsync("azure", LongEnoughContent);

        abstractor.Calls.Should().Be(0, "Off is the byte-identical legacy path — the abstractor stays inert");
        consolidator.Calls.Should().Be(0);
    }

    private static ServiceProvider BuildProvider(
        HarmonicMemoryMode mode,
        IMemoryAbstractor abstractor,
        IMemoryConsolidator consolidator)
    {
        var config = new AppConfig();
        config.AI.Rag.GraphRag.GraphProvider = "in_memory";
        config.AI.HarmonicMemory.Mode = mode;
        config.AI.HarmonicMemory.MinContentLengthChars = 0;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config));
        // The IKnowledgeMemory factory eagerly resolves IFeedbackDetector, which requires IModelRouter.
        services.AddSingleton(Mock.Of<IModelRouter>());
        // KnowledgeScopeAccessor (backing the scoped IKnowledgeScope) requires the ambient agent context.
        services.AddSingleton(Mock.Of<IAgentExecutionContext>());
        // Stand in for the agent-backed implementations a real consumer registers before this call.
        services.AddSingleton(abstractor);
        services.AddSingleton(consolidator);

        services.AddKnowledgeGraphDependencies(config);

        return services.BuildServiceProvider();
    }

    private sealed class RecordingAbstractor : IMemoryAbstractor
    {
        public MemoryAbstraction Result { get; init; } = new() { Abstraction = "default abstraction" };
        public int Calls { get; private set; }

        public Task<MemoryAbstraction> AbstractAsync(string content, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingConsolidator : IMemoryConsolidator
    {
        public int Calls { get; private set; }

        public Task<MemoryConsolidationDecision> ConsolidateAsync(
            MemoryAbstraction candidate,
            string candidateValue,
            IReadOnlyList<ExistingMemory> similarExisting,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(MemoryConsolidationDecision.Create());
        }
    }
}

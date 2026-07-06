using Application.AI.Common.Services.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services.KnowledgeGraph;

public sealed class NotConfiguredHarmonicMemoryDefaultsTests
{
    [Fact]
    public async Task Abstractor_Throws_WithConfigurationGuidance()
    {
        var sut = new NotConfiguredMemoryAbstractor();

        var act = () => sut.AbstractAsync("some content");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("IMemoryAbstractor");
    }

    [Fact]
    public async Task Consolidator_Throws_WithConfigurationGuidance()
    {
        var sut = new NotConfiguredMemoryConsolidator();
        var candidate = new MemoryAbstraction { Abstraction = "a" };

        var act = () => sut.ConsolidateAsync(candidate, "value", []);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("IMemoryConsolidator");
    }
}

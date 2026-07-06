using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Presentation.EvalRunner.HarmonicWriteEval;
using Xunit;

namespace Presentation.EvalRunner.Tests.HarmonicWriteEval;

public sealed class DeterministicMemoryProvidersTests
{
    [Fact]
    public async Task Abstractor_ProducesAbstraction_AndCountsCalls()
    {
        var abstractor = new DeterministicMemoryAbstractor();

        var result = await abstractor.AbstractAsync("Project Orion kicks off in March next year");

        result.Abstraction.Should().NotBeNullOrWhiteSpace();
        abstractor.Calls.Should().Be(1);

        await abstractor.AbstractAsync("another fact");
        abstractor.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Consolidator_NoCandidates_CreatesWithoutCountingACall()
    {
        var consolidator = new DeterministicMemoryConsolidator();

        var decision = await consolidator.ConsolidateAsync(
            new MemoryAbstraction { Abstraction = "solo topic" }, "value", []);

        decision.Action.Should().Be(ConsolidationAction.Create);
        // An empty candidate list does no work (and, for the LLM sibling, no API call), so it is not counted.
        consolidator.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Consolidator_WithCandidates_CountsTheCall()
    {
        var consolidator = new DeterministicMemoryConsolidator();
        var existing = new[] { new ExistingMemory { Id = "id", Abstraction = "some topic", Value = "v" } };

        await consolidator.ConsolidateAsync(new MemoryAbstraction { Abstraction = "other topic" }, "value", existing);

        consolidator.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Consolidator_HighTokenOverlap_MergesIntoBest()
    {
        var consolidator = new DeterministicMemoryConsolidator();
        var existing = new[]
        {
            new ExistingMemory { Id = "id-orion", Abstraction = "project orion launch", Value = "v1" },
            new ExistingMemory { Id = "id-travel", Abstraction = "window seat flights", Value = "v2" }
        };

        // {project, orion, timeline} vs {project, orion, launch} => Jaccard 2/4 = 0.5 >= 0.34 threshold.
        var decision = await consolidator.ConsolidateAsync(
            new MemoryAbstraction { Abstraction = "project orion timeline" }, "value", existing);

        decision.Action.Should().Be(ConsolidationAction.Merge);
        decision.TargetId.Should().Be("id-orion");
    }

    [Fact]
    public async Task Consolidator_LowTokenOverlap_Creates()
    {
        var consolidator = new DeterministicMemoryConsolidator();
        var existing = new[]
        {
            new ExistingMemory { Id = "id-travel", Abstraction = "window seat flights", Value = "v" }
        };

        var decision = await consolidator.ConsolidateAsync(
            new MemoryAbstraction { Abstraction = "vegetarian dietary preference" }, "value", existing);

        decision.Action.Should().Be(ConsolidationAction.Create);
    }
}

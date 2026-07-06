using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Presentation.EvalRunner.HarmonicWriteEval;
using Xunit;

namespace Presentation.EvalRunner.Tests.HarmonicWriteEval;

public sealed class HarmonicWriteEvalAppTests
{
    private static HarmonicWriteFixture SampleFixture() => new()
    {
        Description = "offline test",
        Facts =
        [
            new HarmonicWriteFact { Key = "orion-1", Content = "Project Orion launches in March", GoldTopic = "orion" },
            new HarmonicWriteFact { Key = "orion-2", Content = "Orion project milestone is the API", GoldTopic = "orion" },
            new HarmonicWriteFact { Key = "diet-1", Content = "I am vegetarian and avoid meat", GoldTopic = "diet" },
            new HarmonicWriteFact { Key = "diet-2", Content = "Keep meat out of my meals please", GoldTopic = "diet" }
        ]
    };

    [Fact]
    public async Task RunAsync_Offline_ScoresEachModeWithCorrectCostAndShape()
    {
        await using var emptyProvider = new ServiceCollection().BuildServiceProvider();
        var fixture = SampleFixture();

        var report = await HarmonicWriteEvalApp.RunAsync(
            emptyProvider, fixture, useLlm: false, generatedAtUtc: "2026-01-01T00:00:00Z", CancellationToken.None);

        report.UsedLlm.Should().BeFalse();
        report.FactCount.Should().Be(4);
        report.GoldTopicCount.Should().Be(2);
        report.Modes.Select(m => m.Mode).Should().Equal("Off", "AbstractOnly", "Full");

        var off = report.Modes.Single(m => m.Mode == "Off");
        off.DistinctAbstractions.Should().BeNull("Off produces no abstractions");
        off.FragmentationRatio.Should().BeNull();
        off.ClusterPurity.Should().BeNull();
        off.AbstractorCalls.Should().Be(0);
        off.ConsolidatorCalls.Should().Be(0);
        off.MeanAbstractionQuality.Should().BeNull();

        var abstractOnly = report.Modes.Single(m => m.Mode == "AbstractOnly");
        abstractOnly.AbstractorCalls.Should().Be(4, "every fact is abstracted");
        abstractOnly.ConsolidatorCalls.Should().Be(0, "AbstractOnly never consolidates");
        abstractOnly.DistinctAbstractions.Should().BeInRange(2, 4);
        abstractOnly.MeanAbstractionQuality.Should().BeNull("quality is judged only on paid runs");

        var full = report.Modes.Single(m => m.Mode == "Full");
        full.AbstractorCalls.Should().Be(4);
        full.ConsolidatorCalls.Should().BeGreaterThanOrEqualTo(0);
        full.DistinctAbstractions.Should().NotBeNull();
        full.DistinctAbstractions.Should().BeLessThanOrEqualTo(abstractOnly.DistinctAbstractions!.Value,
            "consolidation can only reduce or hold the distinct-abstraction count, never increase it");
    }
}

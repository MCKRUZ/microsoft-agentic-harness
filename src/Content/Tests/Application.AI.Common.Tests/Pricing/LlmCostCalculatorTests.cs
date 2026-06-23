using Application.AI.Common.Pricing;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Pricing;

/// <summary>
/// Tests for <see cref="LlmCostCalculator"/> — the single source of the LLM cost formula and the
/// model→pricing resolution shared by the usage capture, span processor, and cache-stats middleware.
/// </summary>
public sealed class LlmCostCalculatorTests
{
    private static readonly ModelPricingEntry Sonnet = new()
    {
        Name = "claude-sonnet-4-6",
        InputPerMillion = 3.00m,
        OutputPerMillion = 15.00m,
        CacheReadPerMillion = 0.30m,
        CacheWritePerMillion = 3.75m
    };

    [Fact]
    public void Compute_PricesEachTokenClassAtItsRate()
    {
        // 1M input @3 + 1M output @15 + 1M cache-read @0.30 + 1M cache-write @3.75 = 22.05
        var cost = LlmCostCalculator.Compute(1_000_000, 1_000_000, 1_000_000, 1_000_000, Sonnet);

        cost.Should().Be(22.05m);
    }

    [Fact]
    public void Compute_ZeroTokens_IsZero()
    {
        LlmCostCalculator.Compute(0, 0, 0, 0, Sonnet).Should().Be(0m);
    }

    [Fact]
    public void Compute_NullPricing_Throws()
    {
        var act = () => LlmCostCalculator.Compute(1, 1, 1, 1, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ResolvePricing_KnownModel_ReturnsThatEntry()
    {
        var table = new Dictionary<string, ModelPricingEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [Sonnet.Name] = Sonnet
        };

        LlmCostCalculator.ResolvePricing(table, "claude-sonnet-4-6", "other")
            .Should().BeSameAs(Sonnet);
    }

    [Fact]
    public void ResolvePricing_UnknownModel_FallsBackToDefault()
    {
        var table = new Dictionary<string, ModelPricingEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [Sonnet.Name] = Sonnet
        };

        LlmCostCalculator.ResolvePricing(table, "anthropic/claude-sonnet-4.6", "claude-sonnet-4-6")
            .Should().BeSameAs(Sonnet);
    }

    [Fact]
    public void ResolvePricing_NullModel_FallsBackToDefault()
    {
        var table = new Dictionary<string, ModelPricingEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [Sonnet.Name] = Sonnet
        };

        LlmCostCalculator.ResolvePricing(table, null, "claude-sonnet-4-6")
            .Should().BeSameAs(Sonnet);
    }

    [Fact]
    public void ResolvePricing_NeitherModelNorDefaultPresent_ReturnsNull()
    {
        var table = new Dictionary<string, ModelPricingEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [Sonnet.Name] = Sonnet
        };

        LlmCostCalculator.ResolvePricing(table, "gpt-4o", "gpt-4o").Should().BeNull();
    }
}

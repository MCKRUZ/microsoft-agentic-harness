using Domain.AI.Skills;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Skills;

/// <summary>
/// Tests for <see cref="ContextLoading"/> and <see cref="ContextTierConfig"/>.
/// </summary>
public sealed class ContextLoadingTests
{
    [Fact]
    public void Defaults_AllTiers_AreNull()
    {
        var loading = new ContextLoading();

        loading.Tier1.Should().BeNull();
        loading.Tier2.Should().BeNull();
        loading.Tier3.Should().BeNull();
    }

    [Fact]
    public void HasConfiguration_AllNull_ReturnsFalse()
    {
        new ContextLoading().HasConfiguration.Should().BeFalse();
    }

    [Fact]
    public void HasConfiguration_WithTier1_ReturnsTrue()
    {
        var loading = new ContextLoading { Tier1 = new ContextTierConfig() };

        loading.HasConfiguration.Should().BeTrue();
    }

    [Fact]
    public void HasConfiguration_WithTier2_ReturnsTrue()
    {
        var loading = new ContextLoading { Tier2 = new ContextTierConfig() };

        loading.HasConfiguration.Should().BeTrue();
    }

    [Fact]
    public void HasConfiguration_WithTier3_ReturnsTrue()
    {
        var loading = new ContextLoading { Tier3 = new ContextTierConfig() };

        loading.HasConfiguration.Should().BeTrue();
    }

    [Fact]
    public void RequiresTier1_NullTier_ReturnsFalse()
    {
        new ContextLoading().RequiresTier1.Should().BeFalse();
    }

    [Fact]
    public void RequiresTier1_RequiredTrue_ReturnsTrue()
    {
        var loading = new ContextLoading
        {
            Tier1 = new ContextTierConfig { Required = true }
        };

        loading.RequiresTier1.Should().BeTrue();
    }

    [Fact]
    public void RequiresTier1_RequiredFalse_ReturnsFalse()
    {
        var loading = new ContextLoading
        {
            Tier1 = new ContextTierConfig { Required = false }
        };

        loading.RequiresTier1.Should().BeFalse();
    }

    [Fact]
    public void RequiresTier2_NullTier_ReturnsFalse()
    {
        new ContextLoading().RequiresTier2.Should().BeFalse();
    }

    [Fact]
    public void RequiresTier2_RequiredTrue_ReturnsTrue()
    {
        var loading = new ContextLoading
        {
            Tier2 = new ContextTierConfig { Required = true }
        };

        loading.RequiresTier2.Should().BeTrue();
    }

    [Fact]
    public void HasTier3_NullTier_ReturnsFalse()
    {
        new ContextLoading().HasTier3.Should().BeFalse();
    }

    [Fact]
    public void HasTier3_SetTier_ReturnsTrue()
    {
        var loading = new ContextLoading { Tier3 = new ContextTierConfig() };

        loading.HasTier3.Should().BeTrue();
    }
}

/// <summary>
/// Tests for <see cref="ContextTierConfig"/> — defaults and property assignment.
/// </summary>
public sealed class ContextTierConfigTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var config = new ContextTierConfig();

        config.Required.Should().BeFalse();
        config.Files.Should().BeEmpty();
        config.FromDependencies.Should().BeEmpty();
        config.LookupPaths.Should().BeEmpty();
        config.FallbackPrompt.Should().BeNull();
        config.MaxTokens.Should().BeNull();
    }

    [Fact]
    public void AllProperties_SetExplicitly_RetainValues()
    {
        var config = new ContextTierConfig
        {
            Required = true,
            Files = ["context/org.md"],
            FromDependencies = ["discovery-intake.md"],
            LookupPaths = ["inputs/raw/"],
            FallbackPrompt = "Use file tools to access.",
            MaxTokens = 8000
        };

        config.Required.Should().BeTrue();
        config.Files.Should().HaveCount(1);
        config.FromDependencies.Should().HaveCount(1);
        config.LookupPaths.Should().HaveCount(1);
        config.FallbackPrompt.Should().Be("Use file tools to access.");
        config.MaxTokens.Should().Be(8000);
    }
}

using Application.Core.Validation;
using Domain.Common.Config.AI.Learnings;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="LearningsConfigValidator"/>.
/// Pattern: CreateValidConfig() baseline, mutate one field per test.
/// </summary>
public class LearningsConfigValidatorTests
{
    private readonly LearningsConfigValidator _validator = new();

    [Fact]
    public void DefaultValues_MatchSpec()
    {
        var config = new LearningsConfig();

        config.Enabled.Should().BeTrue();
        config.StoreProvider.Should().Be("graph");
        config.FeedbackAlpha.Should().Be(0.25);
        config.FeedbackCeiling.Should().Be(0.3);
        config.DiversityInjectionRatio.Should().Be(0.15);
        config.VolatileShelfLifeDays.Should().Be(7);
        config.StableShelfLifeDays.Should().Be(180);
        config.PruneIntervalHours.Should().Be(24);
        config.BaselineAdjustmentThreshold.Should().Be(0.8);
        config.BiasCorrection.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_ValidConfig_NoErrors()
    {
        var config = CreateValidConfig();

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_FeedbackAlphaZero_HasError()
    {
        var config = CreateValidConfig();
        config.FeedbackAlpha = 0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "FeedbackAlpha");
    }

    [Fact]
    public async Task Validate_FeedbackAlphaNegative_HasError()
    {
        var config = CreateValidConfig();
        config.FeedbackAlpha = -0.1;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "FeedbackAlpha");
    }

    [Fact]
    public async Task Validate_FeedbackAlphaAboveOne_HasError()
    {
        var config = CreateValidConfig();
        config.FeedbackAlpha = 1.1;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "FeedbackAlpha");
    }

    [Fact]
    public async Task Validate_FeedbackAlphaExactlyOne_Allowed()
    {
        var config = CreateValidConfig();
        config.FeedbackAlpha = 1.0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_FeedbackCeilingZero_HasError()
    {
        var config = CreateValidConfig();
        config.FeedbackCeiling = 0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "FeedbackCeiling");
    }

    [Fact]
    public async Task Validate_FeedbackCeilingNegative_HasError()
    {
        var config = CreateValidConfig();
        config.FeedbackCeiling = -0.5;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "FeedbackCeiling");
    }

    [Fact]
    public async Task Validate_FeedbackCeilingAboveOne_HasError()
    {
        var config = CreateValidConfig();
        config.FeedbackCeiling = 1.5;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "FeedbackCeiling");
    }

    [Fact]
    public async Task Validate_DiversityRatioNegative_HasError()
    {
        var config = CreateValidConfig();
        config.DiversityInjectionRatio = -0.1;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "DiversityInjectionRatio");
    }

    [Fact]
    public async Task Validate_DiversityRatioAboveHalf_HasError()
    {
        var config = CreateValidConfig();
        config.DiversityInjectionRatio = 0.6;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "DiversityInjectionRatio");
    }

    [Fact]
    public async Task Validate_DiversityRatioZero_Allowed()
    {
        var config = CreateValidConfig();
        config.DiversityInjectionRatio = 0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_DiversityRatioExactlyHalf_Allowed()
    {
        var config = CreateValidConfig();
        config.DiversityInjectionRatio = 0.5;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_VolatileShelfLifeZero_HasError()
    {
        var config = CreateValidConfig();
        config.VolatileShelfLifeDays = 0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "VolatileShelfLifeDays");
    }

    [Fact]
    public async Task Validate_VolatileShelfLifeNegative_HasError()
    {
        var config = CreateValidConfig();
        config.VolatileShelfLifeDays = -1;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "VolatileShelfLifeDays");
    }

    [Fact]
    public async Task Validate_StableShelfLifeZero_HasError()
    {
        var config = CreateValidConfig();
        config.StableShelfLifeDays = 0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "StableShelfLifeDays");
    }

    [Fact]
    public async Task Validate_PruneIntervalZero_HasError()
    {
        var config = CreateValidConfig();
        config.PruneIntervalHours = 0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "PruneIntervalHours");
    }

    [Fact]
    public async Task Validate_BaselineAdjustmentThresholdZero_HasError()
    {
        var config = CreateValidConfig();
        config.BaselineAdjustmentThreshold = 0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BaselineAdjustmentThreshold");
    }

    [Fact]
    public async Task Validate_BaselineAdjustmentThresholdAboveOne_HasError()
    {
        var config = CreateValidConfig();
        config.BaselineAdjustmentThreshold = 1.1;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BaselineAdjustmentThreshold");
    }

    [Fact]
    public async Task Validate_EmptyStoreProvider_HasError()
    {
        var config = CreateValidConfig();
        config.StoreProvider = "";

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "StoreProvider");
    }

    [Fact]
    public void BindsFromAppSettingsJson()
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["AppConfig:AI:Learnings:Enabled"] = "false",
            ["AppConfig:AI:Learnings:StoreProvider"] = "in_memory",
            ["AppConfig:AI:Learnings:FeedbackAlpha"] = "0.5",
            ["AppConfig:AI:Learnings:FeedbackCeiling"] = "0.4",
            ["AppConfig:AI:Learnings:DiversityInjectionRatio"] = "0.2",
            ["AppConfig:AI:Learnings:VolatileShelfLifeDays"] = "14",
            ["AppConfig:AI:Learnings:StableShelfLifeDays"] = "365",
            ["AppConfig:AI:Learnings:PruneIntervalHours"] = "12",
            ["AppConfig:AI:Learnings:BaselineAdjustmentThreshold"] = "0.9",
            ["AppConfig:AI:Learnings:BiasCorrection"] = "false"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemory)
            .Build();

        var config = configuration
            .GetSection("AppConfig:AI:Learnings")
            .Get<LearningsConfig>()!;

        config.Enabled.Should().BeFalse();
        config.StoreProvider.Should().Be("in_memory");
        config.FeedbackAlpha.Should().Be(0.5);
        config.FeedbackCeiling.Should().Be(0.4);
        config.DiversityInjectionRatio.Should().Be(0.2);
        config.VolatileShelfLifeDays.Should().Be(14);
        config.StableShelfLifeDays.Should().Be(365);
        config.PruneIntervalHours.Should().Be(12);
        config.BaselineAdjustmentThreshold.Should().Be(0.9);
        config.BiasCorrection.Should().BeFalse();
    }

    private static LearningsConfig CreateValidConfig() => new()
    {
        Enabled = true,
        StoreProvider = "graph",
        FeedbackAlpha = 0.25,
        FeedbackCeiling = 0.3,
        DiversityInjectionRatio = 0.15,
        VolatileShelfLifeDays = 7,
        StableShelfLifeDays = 180,
        PruneIntervalHours = 24,
        BaselineAdjustmentThreshold = 0.8,
        BiasCorrection = true
    };
}

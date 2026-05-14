using Application.Core.Validation;
using Domain.Common.Config.AI.DriftDetection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="DriftDetectionConfigValidator"/>.
/// Pattern: CreateValidConfig() baseline, mutate one field per test.
/// </summary>
public class DriftDetectionConfigValidatorTests
{
    private readonly DriftDetectionConfigValidator _validator = new();

    [Fact]
    public void DefaultValues_MatchSpec()
    {
        var config = new DriftDetectionConfig();

        config.Enabled.Should().BeTrue();
        config.EwmaLambda.Should().Be(0.2);
        config.ControlLimitWidth.Should().Be(3.0);
        config.MinSamplesForBaseline.Should().Be(20);
        config.BaselineWindowDays.Should().Be(7);
        config.WarnThresholdSigma.Should().Be(1.5);
        config.AlertThresholdSigma.Should().Be(2.5);
        config.EscalateThresholdSigma.Should().Be(3.0);
        config.EscalationEnabled.Should().BeTrue();
        config.AuditPath.Should().Be("data/audit");
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
    public async Task Validate_EwmaLambdaZero_HasError()
    {
        var config = CreateValidConfig();
        config.EwmaLambda = 0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "EwmaLambda");
    }

    [Fact]
    public async Task Validate_EwmaLambdaNegative_HasError()
    {
        var config = CreateValidConfig();
        config.EwmaLambda = -0.1;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "EwmaLambda");
    }

    [Fact]
    public async Task Validate_EwmaLambdaGreaterThanOne_HasError()
    {
        var config = CreateValidConfig();
        config.EwmaLambda = 1.1;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "EwmaLambda");
    }

    [Fact]
    public async Task Validate_EwmaLambdaExactlyOne_NoError()
    {
        var config = CreateValidConfig();
        config.EwmaLambda = 1.0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WarnThresholdGreaterThanOrEqualToAlert_HasError()
    {
        var config = CreateValidConfig();
        config.WarnThresholdSigma = 2.5;
        config.AlertThresholdSigma = 2.5;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "WarnThresholdSigma");
    }

    [Fact]
    public async Task Validate_AlertThresholdGreaterThanOrEqualToEscalate_HasError()
    {
        var config = CreateValidConfig();
        config.AlertThresholdSigma = 3.0;
        config.EscalateThresholdSigma = 3.0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "AlertThresholdSigma");
    }

    [Fact]
    public async Task Validate_MinSamplesForBaselineZero_HasError()
    {
        var config = CreateValidConfig();
        config.MinSamplesForBaseline = 0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "MinSamplesForBaseline");
    }

    [Fact]
    public async Task Validate_NegativeControlLimitWidth_HasError()
    {
        var config = CreateValidConfig();
        config.ControlLimitWidth = -1.0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ControlLimitWidth");
    }

    [Fact]
    public async Task Validate_BaselineWindowDaysZero_HasError()
    {
        var config = CreateValidConfig();
        config.BaselineWindowDays = 0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BaselineWindowDays");
    }

    [Fact]
    public async Task Validate_EmptyAuditPath_HasError()
    {
        var config = CreateValidConfig();
        config.AuditPath = "";

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "AuditPath");
    }

    [Fact]
    public async Task Validate_WarnThresholdSigmaZero_HasError()
    {
        var config = CreateValidConfig();
        config.WarnThresholdSigma = 0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "WarnThresholdSigma");
    }

    [Fact]
    public async Task Validate_AlertThresholdSigmaZero_HasError()
    {
        var config = CreateValidConfig();
        config.AlertThresholdSigma = 0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "AlertThresholdSigma");
    }

    [Fact]
    public async Task Validate_EscalateThresholdSigmaZero_HasError()
    {
        var config = CreateValidConfig();
        config.EscalateThresholdSigma = 0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "EscalateThresholdSigma");
    }

    [Fact]
    public void BindsFromAppSettingsJson()
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["AppConfig:AI:DriftDetection:Enabled"] = "false",
            ["AppConfig:AI:DriftDetection:EwmaLambda"] = "0.3",
            ["AppConfig:AI:DriftDetection:ControlLimitWidth"] = "2.5",
            ["AppConfig:AI:DriftDetection:MinSamplesForBaseline"] = "30",
            ["AppConfig:AI:DriftDetection:BaselineWindowDays"] = "14",
            ["AppConfig:AI:DriftDetection:WarnThresholdSigma"] = "1.0",
            ["AppConfig:AI:DriftDetection:AlertThresholdSigma"] = "2.0",
            ["AppConfig:AI:DriftDetection:EscalateThresholdSigma"] = "2.8",
            ["AppConfig:AI:DriftDetection:EscalationEnabled"] = "false",
            ["AppConfig:AI:DriftDetection:AuditPath"] = "logs/drift-audit"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemory)
            .Build();

        var config = configuration
            .GetSection("AppConfig:AI:DriftDetection")
            .Get<DriftDetectionConfig>()!;

        config.Enabled.Should().BeFalse();
        config.EwmaLambda.Should().Be(0.3);
        config.ControlLimitWidth.Should().Be(2.5);
        config.MinSamplesForBaseline.Should().Be(30);
        config.BaselineWindowDays.Should().Be(14);
        config.WarnThresholdSigma.Should().Be(1.0);
        config.AlertThresholdSigma.Should().Be(2.0);
        config.EscalateThresholdSigma.Should().Be(2.8);
        config.EscalationEnabled.Should().BeFalse();
        config.AuditPath.Should().Be("logs/drift-audit");
    }

    private static DriftDetectionConfig CreateValidConfig() => new()
    {
        Enabled = true,
        EwmaLambda = 0.2,
        ControlLimitWidth = 3.0,
        MinSamplesForBaseline = 20,
        BaselineWindowDays = 7,
        WarnThresholdSigma = 1.5,
        AlertThresholdSigma = 2.5,
        EscalateThresholdSigma = 3.0,
        EscalationEnabled = true,
        AuditPath = "data/audit"
    };
}

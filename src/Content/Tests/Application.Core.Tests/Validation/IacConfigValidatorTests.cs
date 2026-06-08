using Application.Core.Validation;
using Domain.Common.Config.AI.Iac;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="IacConfigValidator"/>. All rules are conditional on
/// <see cref="IacConfig.Enabled"/>: a disabled skill pack is always valid; an
/// enabled one mirrors the boot-time checks in <c>IacStartupValidator</c>.
/// Pattern: CreateValidConfig() baseline, mutate one field per test.
/// </summary>
public class IacConfigValidatorTests
{
    private readonly IacConfigValidator _validator = new();

    [Fact]
    public void DefaultValues_MatchSpec()
    {
        var config = new IacConfig();

        config.Enabled.Should().BeFalse();
        config.EnabledBackends.Should().Equal("terraform", "bicep");
        config.BlockingSeverity.Should().Be("High");
        config.RegistryAllowlist.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Validate_Disabled_AlwaysValid()
    {
        var config = new IacConfig
        {
            Enabled = false,
            EnabledBackends = [],
            BlockingSeverity = "nonsense",
            RegistryAllowlist = [],
            TerraformVersion = ""
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_ValidConfig_NoErrors()
    {
        var result = await _validator.ValidateAsync(CreateValidConfig());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_EmptyBackends_HasError()
    {
        var config = CreateValidConfig();
        config.EnabledBackends = [];

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "EnabledBackends");
    }

    [Fact]
    public async Task Validate_UnknownBackend_HasError()
    {
        var config = CreateValidConfig();
        config.EnabledBackends = ["terraform", "pulumi"];

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "EnabledBackends");
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("")]
    public async Task Validate_InvalidBlockingSeverity_HasError(string severity)
    {
        var config = CreateValidConfig();
        config.BlockingSeverity = severity;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BlockingSeverity");
    }

    [Theory]
    [InlineData("Low")]
    [InlineData("medium")]
    [InlineData("HIGH")]
    [InlineData("Critical")]
    public async Task Validate_ValidBlockingSeverityCaseInsensitive_NoSeverityError(string severity)
    {
        var config = CreateValidConfig();
        config.BlockingSeverity = severity;

        var result = await _validator.ValidateAsync(config);

        result.Errors.Should().NotContain(e => e.PropertyName == "BlockingSeverity");
    }

    [Fact]
    public async Task Validate_EmptyRegistryAllowlist_HasError()
    {
        var config = CreateValidConfig();
        config.RegistryAllowlist = [];

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "RegistryAllowlist");
    }

    [Fact]
    public async Task Validate_TerraformEnabledButTerraformVersionEmpty_HasError()
    {
        var config = CreateValidConfig();
        config.TerraformVersion = "";

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "TerraformVersion");
    }

    [Fact]
    public async Task Validate_BicepEnabledButArmTtkVersionEmpty_HasError()
    {
        var config = CreateValidConfig();
        config.ArmTtkVersion = "";

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ArmTtkVersion");
    }

    [Fact]
    public async Task Validate_OnlyTerraformEnabled_DoesNotRequireBicepVersion()
    {
        var config = CreateValidConfig();
        config.EnabledBackends = ["terraform"];
        config.BicepVersion = "";
        config.ArmTtkVersion = "";

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    private static IacConfig CreateValidConfig() => new()
    {
        Enabled = true,
        EnabledBackends = ["terraform", "bicep"],
        TerraformVersion = "1.9.5",
        BicepVersion = "0.30.23",
        CheckovVersion = "3.2.0",
        TfsecVersion = "1.28.11",
        ArmTtkVersion = "0.24",
        BlockingSeverity = "High",
        RegistryAllowlist = ["registry.terraform.io"]
    };
}

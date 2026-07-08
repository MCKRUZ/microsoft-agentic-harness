using Application.Core.Validation;
using Domain.Common.Config.AI;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="GovernanceConfigValidator"/>. The default section is valid (so omitted /
/// default hosts keep booting); the landmine rules fire only when governance is disabled but a
/// kernel-path-only feature is switched on. Pattern: a valid baseline, mutate one field per test.
/// </summary>
public class GovernanceConfigValidatorTests
{
    private readonly GovernanceConfigValidator _validator = new();

    [Fact]
    public async Task Validate_DefaultConfig_IsValid()
    {
        var result = await _validator.ValidateAsync(new GovernanceConfig());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_EnabledWithInjectionDetectionOff_IsValid()
    {
        // The exact combination the composition crash fix makes valid: governance on, detection off.
        var config = new GovernanceConfig { Enabled = true, EnablePromptInjectionDetection = false };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_EnabledWithAllFeaturesOn_IsValid()
    {
        // Mirrors the shape every host ships today.
        var config = new GovernanceConfig
        {
            Enabled = true,
            EnablePromptInjectionDetection = true,
            EnableMcpSecurity = true,
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_DisabledWithInjectionDetectionOn_HasError()
    {
        var config = new GovernanceConfig { Enabled = false, EnablePromptInjectionDetection = true };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GovernanceConfig.EnablePromptInjectionDetection));
    }

    [Fact]
    public async Task Validate_DisabledWithMcpSecurityOn_HasError()
    {
        var config = new GovernanceConfig { Enabled = false, EnableMcpSecurity = true };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GovernanceConfig.EnableMcpSecurity));
    }

    [Fact]
    public async Task Validate_DisabledWithEnforceToolInvocationOn_IsValid()
    {
        // EnforceToolInvocation is consumed on the live tool path independent of Enabled, so it is
        // intentionally not constrained by the landmine guard.
        var config = new GovernanceConfig { Enabled = false, EnforceToolInvocation = true };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Validate_BlankPolicyPath_HasError(string blankPath)
    {
        var config = new GovernanceConfig { PolicyPaths = [blankPath] };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.StartsWith(nameof(GovernanceConfig.PolicyPaths)));
    }

    [Fact]
    public async Task Validate_OutOfRangeConflictStrategy_HasError()
    {
        var config = new GovernanceConfig { ConflictStrategy = (ConflictResolutionStrategy)999 };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GovernanceConfig.ConflictStrategy));
    }

    [Fact]
    public async Task Validate_OutOfRangeInjectionBlockThreshold_HasError()
    {
        var config = new GovernanceConfig { InjectionBlockThreshold = (ThreatLevel)999 };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GovernanceConfig.InjectionBlockThreshold));
    }
}

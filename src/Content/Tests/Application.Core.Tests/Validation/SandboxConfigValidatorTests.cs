using Application.Core.Validation;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="SandboxConfigValidator"/> (#647) — the startup form of
/// <c>CapabilityEnforcer.WarnIfPatternIsInert</c>'s runtime check.
/// </summary>
public class SandboxConfigValidatorTests
{
    private readonly SandboxConfigValidator _validator = new();

    [Fact]
    public async Task Validate_NoToolOverrides_NoErrors()
    {
        var config = new SandboxConfig();

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WellFormedHostPatterns_NoErrors()
    {
        var config = new SandboxConfig();
        config.ToolOverrides["curl"] = new ToolOverrideConfig
        {
            DeniedHosts = ["169.254.169.254", "*.internal.example.com"],
            AllowedHosts = ["api.example.com"],
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_DeniedHostsEntryNormalizesToInvalidHost_HasError()
    {
        // A value carrying an embedded NUL fails SecureInputValidatorHelper.ValidateHost outright and
        // cannot arise from any legitimate hostname — the same shape the runtime check treats as inert.
        var config = new SandboxConfig();
        config.ToolOverrides["curl"] = new ToolOverrideConfig
        {
            DeniedHosts = ["evil\0.example.com"],
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e =>
            e.ErrorMessage.Contains("AppConfig:AI:SandboxCapabilities:ToolOverrides:curl:DeniedHosts", StringComparison.Ordinal)
            && e.ErrorMessage.Contains("permanent, silent no-op", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_AllowedHostsEntryNormalizesToInvalidHost_HasError()
    {
        var config = new SandboxConfig();
        config.ToolOverrides["curl"] = new ToolOverrideConfig
        {
            AllowedHosts = ["evil\0.example.com"],
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e =>
            e.ErrorMessage.Contains("AppConfig:AI:SandboxCapabilities:ToolOverrides:curl:AllowedHosts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_WildcardPrefixedInvalidHost_HasError()
    {
        // The wildcard prefix itself must be stripped before validating, exactly like the runtime
        // check — otherwise "*." would be validated as part of the host and every wildcard entry
        // would spuriously fail.
        var config = new SandboxConfig();
        config.ToolOverrides["curl"] = new ToolOverrideConfig
        {
            DeniedHosts = ["*.evil\0.example.com"],
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_NullEntryInHostList_IsTreatedAsValid()
    {
        // A literal JSON `null` binds into a null List<string> element despite the non-nullable
        // element type. The runtime check skips it before normalization; this validator must too,
        // rather than crashing or flagging a config-binding artifact as an operator typo.
        var config = new SandboxConfig();
        config.ToolOverrides["curl"] = new ToolOverrideConfig
        {
            DeniedHosts = [null!],
        };

        var act = async () => await _validator.ValidateAsync(config);

        (await act.Should().NotThrowAsync()).Which.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_MultipleToolsOneBad_NamesTheOffendingToolInTheMessage()
    {
        var config = new SandboxConfig();
        config.ToolOverrides["curl"] = new ToolOverrideConfig { DeniedHosts = ["good.example.com"] };
        config.ToolOverrides["kubectl"] = new ToolOverrideConfig { DeniedHosts = ["evil\0.example.com"] };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.ErrorMessage.Contains(":kubectl:", StringComparison.Ordinal));
        result.Errors.Should().NotContain(e => e.ErrorMessage.Contains(":curl:", StringComparison.Ordinal));
    }
}

using Application.Core.Validation;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="DataClassificationConfigValidator"/>. All rules are conditional on
/// <see cref="DataClassificationConfig.Mode"/> being non-<c>Off</c>: a disabled (off) section is always
/// valid; an enabled one rejects blank label keys that could never match a real Purview label. Pattern:
/// a valid baseline, mutate one field per test.
/// </summary>
public class DataClassificationConfigValidatorTests
{
    private readonly DataClassificationConfigValidator _validator = new();

    [Fact]
    public void DefaultValues_AreOffWithAllowDefaults()
    {
        var config = new DataClassificationConfig();

        config.Mode.Should().Be(ClassificationEnforcementMode.Off);
        config.DefaultAction.Should().Be(ClassificationAction.Allow);
        config.UnknownAssetAction.Should().Be(ClassificationAction.Allow);
        config.LabelActions.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_OffWithBlankLabelKey_IsValid()
    {
        // Mode=Off short-circuits every rule, so even an incoherent map is accepted.
        var config = new DataClassificationConfig
        {
            Mode = ClassificationEnforcementMode.Off,
            LabelActions = new() { ["  "] = ClassificationAction.Block },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_AuditWithEmptyLabelActions_IsValid()
    {
        // An empty map is coherent: every resolved label falls through to DefaultAction.
        var config = new DataClassificationConfig { Mode = ClassificationEnforcementMode.Audit };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_EnforceWithNamedLabelAction_IsValid()
    {
        var config = new DataClassificationConfig
        {
            Mode = ClassificationEnforcementMode.Enforce,
            LabelActions = new() { ["Confidential"] = ClassificationAction.Block },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Validate_EnforceWithBlankLabelKey_HasError(string blankKey)
    {
        var config = new DataClassificationConfig
        {
            Mode = ClassificationEnforcementMode.Enforce,
            LabelActions = new() { [blankKey] = ClassificationAction.Block },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.StartsWith("LabelActions"));
    }

    [Fact]
    public async Task Validate_EnforceWithNegativeResultCacheTtl_HasError()
    {
        var config = new DataClassificationConfig
        {
            Mode = ClassificationEnforcementMode.Enforce,
            ResultCacheTtl = TimeSpan.FromSeconds(-1),
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(DataClassificationConfig.ResultCacheTtl));
    }

    [Fact]
    public async Task Validate_InformationProtectionDisabled_SkipsProviderRules()
    {
        // Even with incoherent provider settings, a disabled provider imposes no constraints.
        var config = new DataClassificationConfig
        {
            InformationProtection = new InformationProtectionProviderConfig
            {
                Enabled = false,
                GraphBaseUrl = "not-a-url",
                Scopes = [],
                LabelCatalogCacheTtl = TimeSpan.Zero,
            },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_InformationProtectionEnabledWithDefaults_IsValid()
    {
        var config = new DataClassificationConfig
        {
            InformationProtection = new InformationProtectionProviderConfig { Enabled = true },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://graph.microsoft.com")]
    [InlineData("graph.microsoft.com/v1.0")]
    public async Task Validate_InformationProtectionEnabledWithInvalidGraphUrl_HasError(string badUrl)
    {
        var config = new DataClassificationConfig
        {
            InformationProtection = new InformationProtectionProviderConfig { Enabled = true, GraphBaseUrl = badUrl },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains(nameof(InformationProtectionProviderConfig.GraphBaseUrl)));
    }

    [Fact]
    public async Task Validate_InformationProtectionEnabledWithEmptyScopes_HasError()
    {
        var config = new DataClassificationConfig
        {
            InformationProtection = new InformationProtectionProviderConfig { Enabled = true, Scopes = [] },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains(nameof(InformationProtectionProviderConfig.Scopes)));
    }

    [Fact]
    public async Task Validate_InformationProtectionEnabledWithNonPositiveCatalogTtl_HasError()
    {
        var config = new DataClassificationConfig
        {
            InformationProtection = new InformationProtectionProviderConfig
            {
                Enabled = true,
                LabelCatalogCacheTtl = TimeSpan.Zero,
            },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains(nameof(InformationProtectionProviderConfig.LabelCatalogCacheTtl)));
    }
}

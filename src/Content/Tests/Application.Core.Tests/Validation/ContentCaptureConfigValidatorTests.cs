using Application.Core.Validation;
using Domain.Common.Config.AI.Telemetry;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="ContentCaptureConfigValidator"/>. All rules are
/// conditional on <see cref="ContentCaptureConfig.Enabled"/>: a disabled section
/// is always valid; an enabled one requires a non-empty list of known
/// <c>RedactionCategory</c> names. Pattern: a valid baseline, mutate one field
/// per test.
/// </summary>
public class ContentCaptureConfigValidatorTests
{
    private readonly ContentCaptureConfigValidator _validator = new();

    [Fact]
    public void DefaultValues_AreCaptureOffWithAllCategories()
    {
        var config = new ContentCaptureConfig();

        config.Enabled.Should().BeFalse();
        config.RedactionCategories.Should()
            .BeEquivalentTo("Email", "Phone", "Ssn", "CreditCard", "IpAddress", "AwsKey", "JwtToken", "Generic");
    }

    [Fact]
    public async Task Validate_DisabledWithEmptyCategories_IsValid()
    {
        // Categories are empty, but Enabled=false short-circuits every rule.
        var config = new ContentCaptureConfig
        {
            Enabled = false,
            RedactionCategories = [],
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_DisabledWithUnknownCategory_IsValid()
    {
        var config = new ContentCaptureConfig
        {
            Enabled = false,
            RedactionCategories = ["NotARealCategory"],
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_EnabledWithDefaultCategories_IsValid()
    {
        var config = new ContentCaptureConfig { Enabled = true };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_EnabledWithEmptyCategories_HasError()
    {
        var config = new ContentCaptureConfig
        {
            Enabled = true,
            RedactionCategories = [],
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "RedactionCategories");
    }

    [Theory]
    [InlineData("2")]                   // the numeric form of a real category
    [InlineData(" 2")]                  // and behind a stray space
    [InlineData("99")]                  // outside the defined range
    [InlineData("Email,Ssn")]           // comma-composite
    public async Task Validate_EnabledWithNonNameCategory_HasError(string entry)
    {
        // #300. This validator and ContentCapturePolicy now share one reader, so the boot check and
        // the runtime read agree on what counts as a category. Both refuse these; before the sweep
        // the validator refused them and the runtime accepted them, which meant a numeric entry
        // passed silently wherever the validator was not wired.
        var config = new ContentCaptureConfig
        {
            Enabled = true,
            RedactionCategories = ["Email", entry],
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.StartsWith("RedactionCategories"));
    }

    [Fact]
    public async Task Validate_EnabledWithUnknownCategory_HasError()
    {
        var config = new ContentCaptureConfig
        {
            Enabled = true,
            RedactionCategories = ["Email", "Bogus"],
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.StartsWith("RedactionCategories"));
    }

    [Theory]
    [InlineData("email")]
    [InlineData("EMAIL")]
    [InlineData("Email")]
    [InlineData("jwttoken")]
    public async Task Validate_EnabledWithKnownCategoryCaseInsensitive_NoCategoryError(string category)
    {
        var config = new ContentCaptureConfig
        {
            Enabled = true,
            RedactionCategories = [category],
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_EnabledWithWhitespacePaddedKnownCategory_IsValid()
    {
        var config = new ContentCaptureConfig
        {
            Enabled = true,
            RedactionCategories = [" Email "],
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_EnabledWithAllKnownCategories_IsValid()
    {
        var config = new ContentCaptureConfig
        {
            Enabled = true,
            RedactionCategories =
                ["Email", "Phone", "Ssn", "CreditCard", "IpAddress", "AwsKey", "JwtToken", "Generic"],
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }
}

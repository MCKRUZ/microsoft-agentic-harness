using Application.Core.Validation;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="LogsConfigValidator"/>. All rules are conditional on
/// <see cref="LogsConfig.OtelExportEnabled"/>: a disabled section is always valid
/// (the signal is off by default); an enabled one requires a parseable
/// <c>MinExportLevel</c> and — when redaction is on — a non-empty list of known
/// <c>RedactionCategory</c> names. Pattern: a valid baseline, mutate one field per test.
/// </summary>
public class LogsConfigValidatorTests
{
    private readonly LogsConfigValidator _validator = new();

    [Fact]
    public void DefaultValues_AreExportOffWithSafeDefaults()
    {
        var config = new LogsConfig();

        config.OtelExportEnabled.Should().BeFalse();
        config.MinExportLevel.Should().Be("Information");
        config.RedactionEnabled.Should().BeTrue();
        config.RedactionCategories.Should()
            .BeEquivalentTo("Email", "Phone", "Ssn", "CreditCard", "IpAddress", "AwsKey", "JwtToken", "Generic");
    }

    [Fact]
    public async Task Validate_DisabledWithGarbageValues_IsValid()
    {
        // Export off short-circuits every rule, so an omitted/off section always boots.
        var config = new LogsConfig
        {
            OtelExportEnabled = false,
            MinExportLevel = "NotALevel",
            RedactionCategories = ["Bogus"],
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_EnabledWithDefaults_IsValid()
    {
        var config = new LogsConfig { OtelExportEnabled = true };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Trace")]
    [InlineData("debug")]
    [InlineData("INFORMATION")]
    [InlineData("Warning")]
    [InlineData("Error")]
    [InlineData("Critical")]
    [InlineData("None")]
    public async Task Validate_EnabledWithValidLevelCaseInsensitive_NoLevelError(string level)
    {
        var config = new LogsConfig { OtelExportEnabled = true, MinExportLevel = level };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("Verbose")]
    [InlineData("NotALevel")]
    [InlineData("")]
    public async Task Validate_EnabledWithInvalidLevel_HasError(string level)
    {
        var config = new LogsConfig { OtelExportEnabled = true, MinExportLevel = level };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(LogsConfig.MinExportLevel));
    }

    [Fact]
    public async Task Validate_EnabledRedactionWithEmptyCategories_HasError()
    {
        var config = new LogsConfig
        {
            OtelExportEnabled = true,
            RedactionEnabled = true,
            RedactionCategories = [],
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(LogsConfig.RedactionCategories));
    }

    [Fact]
    public async Task Validate_EnabledRedactionWithUnknownCategory_HasError()
    {
        var config = new LogsConfig
        {
            OtelExportEnabled = true,
            RedactionEnabled = true,
            RedactionCategories = ["Email", "Bogus"],
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.StartsWith(nameof(LogsConfig.RedactionCategories)));
    }

    [Fact]
    public async Task Validate_EnabledButRedactionOff_EmptyCategoriesAllowed()
    {
        // With redaction off, the category rules don't fire (the operator has opted out).
        var config = new LogsConfig
        {
            OtelExportEnabled = true,
            RedactionEnabled = false,
            RedactionCategories = [],
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }
}

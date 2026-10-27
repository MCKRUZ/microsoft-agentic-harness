using Application.Core.Validation;
using Domain.Common.Config.AI.ContentSafety;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// <see cref="ContentSafetyConfigValidator"/> (#904): the allow-all default needs nothing; Azure AI Content Safety needs
/// what the service needs, checked at startup rather than discovered as every request blocked.
/// </summary>
public sealed class ContentSafetyConfigValidatorTests
{
    private readonly ContentSafetyConfigValidator _validator = new();

    private static ContentSafetyConfig Azure() => new()
    {
        Provider = ContentSafetyProvider.AzureAIContentSafety,
        Endpoint = "https://example.cognitiveservices.azure.com/",
    };

    [Fact]
    public void Validate_Defaults_AreValid()
    {
        _validator.Validate(new ContentSafetyConfig()).IsValid.Should().BeTrue("a host that sets nothing must keep booting");
    }

    [Fact]
    public void Validate_AzureWithAnHttpsEndpoint_IsValid()
    {
        _validator.Validate(Azure()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://example.cognitiveservices.azure.com/")]
    public void Validate_AzureWithoutAnHttpsEndpoint_Fails(string? endpoint)
    {
        var config = Azure();
        config.Endpoint = endpoint;

        _validator.Validate(config).Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("Endpoint");
    }

    [Fact]
    public void Validate_AzureWithNoCategories_IsValidAndScreensAllFour()
    {
        var config = Azure();
        config.Categories = [];

        _validator.Validate(config).IsValid.Should().BeTrue();
        config.ScreenedCategories.Should().Equal(ContentSafetyConfig.AllCategories);
    }

    [Fact]
    public void Validate_AzureWithADuplicatedCategory_Fails()
    {
        var config = Azure();
        config.Categories = ["Hate", "Hate"];

        _validator.Validate(config).Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("more than once");
    }

    [Fact]
    public void Validate_AzureWithAnUnknownCategory_Fails()
    {
        var config = Azure();
        config.Categories = ["Hate", "Profanity"];

        _validator.Validate(config).Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("unknown category");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void Validate_AzureWithAThresholdTheServiceNeverReturns_Fails(int severity)
    {
        // The service scores at 0, 2, 4 or 6, so 3 would behave exactly as 4: a setting with no effect of its own.
        var config = Azure();
        config.BlockAtSeverity = severity;

        _validator.Validate(config).Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("BlockAtSeverity");
    }

    [Fact]
    public void Binding_ConfiguredCategories_ReplaceTheDefaultRatherThanAddingToIt()
    {
        // Configuration binding appends to a list that already holds items. A default of all four names would turn a
        // configured ["Hate"] into five entries, so a deployment could never narrow the screen.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Categories:0"] = "Hate" })
            .Build();

        var config = configuration.Get<ContentSafetyConfig>()!;

        config.ScreenedCategories.Should().Equal("Hate");
    }
}

using Application.Core.Validation;
using Domain.Common.Config.AI;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="RemoteMemoryConfigValidator"/>. All rules are conditional on
/// <see cref="RemoteMemoryConfig.Enabled"/>: a disabled (default) config is always valid; an
/// enabled one requires a non-empty https:// BaseUrl, ApiKey, AvatarId, and a positive timeout.
/// </summary>
public sealed class RemoteMemoryConfigValidatorTests
{
    private readonly RemoteMemoryConfigValidator _validator = new();

    [Fact]
    public async Task Validate_Disabled_AlwaysValid()
    {
        // Everything is blank/invalid, but Enabled=false short-circuits all rules.
        var config = new RemoteMemoryConfig
        {
            Enabled = false,
            BaseUrl = "http://insecure.example.com",
            ApiKey = "",
            AvatarId = "",
            TimeoutSeconds = -1,
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_ValidEnabledConfig_NoErrors()
    {
        var result = await _validator.ValidateAsync(CreateValidConfig());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://insecure.example.com")]
    [InlineData("not-a-url")]
    [InlineData("ftp://avatar.example.com")]
    public async Task Validate_EnabledWithNonHttpsBaseUrl_HasError(string baseUrl)
    {
        var config = CreateValidConfig();
        config.BaseUrl = baseUrl;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BaseUrl");
    }

    [Fact]
    public async Task Validate_EnabledWithEmptyApiKey_HasError()
    {
        var config = CreateValidConfig();
        config.ApiKey = "";

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ApiKey");
    }

    [Fact]
    public async Task Validate_EnabledWithEmptyAvatarId_HasError()
    {
        var config = CreateValidConfig();
        config.AvatarId = "";

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "AvatarId");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Validate_EnabledWithNonPositiveTimeout_HasError(int timeoutSeconds)
    {
        var config = CreateValidConfig();
        config.TimeoutSeconds = timeoutSeconds;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "TimeoutSeconds");
    }

    [Fact]
    public async Task Validate_EnabledWithoutAcknowledgingSharedRecallBoundary_HasError()
    {
        var config = CreateValidConfig();
        config.AcknowledgeSharedRecallBoundary = false;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "AcknowledgeSharedRecallBoundary");
    }

    private static RemoteMemoryConfig CreateValidConfig() => new()
    {
        Enabled = true,
        BaseUrl = "https://avatar.example.com",
        ApiKey = "secret-key",
        AvatarId = "avatar-1",
        TimeoutSeconds = 10,
        AcknowledgeSharedRecallBoundary = true,
    };
}

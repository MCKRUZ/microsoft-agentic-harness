using Application.Core.Validation;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// The telemetry key (#903): unset is valid (a random key is made at startup); set, it must be a usable HMAC key, because
/// it covers every telemetry pseudonym and a mistyped one would quietly hash under a key nobody meant.
/// </summary>
public sealed class PiiFilteringConfigValidatorTests
{
    private readonly PiiFilteringConfigValidator _validator = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=")] // 32 bytes
    public void Validate_KeyUnsetOrUsable_IsValid(string? key)
    {
        _validator.Validate(new PiiFilteringConfig { HmacKey = key }).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("AAECAwQFBgcICQoLDA0ODw==")] // 16 bytes
    [InlineData("   ")]
    public void Validate_KeySetButUnusable_FailsNamingTheSetting(string key)
    {
        var result = _validator.Validate(new PiiFilteringConfig { HmacKey = key });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("HmacKey").And.Contain("32 bytes");
    }
}

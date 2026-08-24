using Application.AI.Common.Services;
using Application.Core.Validation;
using Domain.Common.Config.AI.Conversations;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="ToolCallReplayConfigValidator"/>. The security invariant under test:
/// <see cref="ToolCallReplayConfig.MaxVerbatimChars"/> must never validate above
/// <see cref="ToolCallReplayTreatment.WithholdCeilingChars"/> — the point where structural
/// secret-redaction stops being trustworthy.
/// </summary>
public sealed class ToolCallReplayConfigValidatorTests
{
    private readonly ToolCallReplayConfigValidator _validator = new();

    [Fact]
    public async Task Validate_DefaultConfig_IsValid()
    {
        var result = await _validator.ValidateAsync(new ToolCallReplayConfig());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_MaxVerbatimCharsZero_IsValid()
    {
        var result = await _validator.ValidateAsync(new ToolCallReplayConfig { MaxVerbatimChars = 0 });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_MaxVerbatimCharsAtWithholdCeiling_IsValid()
    {
        var result = await _validator.ValidateAsync(
            new ToolCallReplayConfig { MaxVerbatimChars = ToolCallReplayTreatment.WithholdCeilingChars });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_MaxVerbatimCharsAboveWithholdCeiling_HasError()
    {
        var result = await _validator.ValidateAsync(
            new ToolCallReplayConfig { MaxVerbatimChars = ToolCallReplayTreatment.WithholdCeilingChars + 1 });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "MaxVerbatimChars");
    }

    [Fact]
    public async Task Validate_MaxVerbatimCharsNegative_HasError()
    {
        var result = await _validator.ValidateAsync(new ToolCallReplayConfig { MaxVerbatimChars = -1 });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "MaxVerbatimChars");
    }

    [Fact]
    public void DefaultValue_IsWellUnderTheWithholdCeiling()
    {
        // Regression guard for the default itself, not just the validator: if a future edit raises
        // ToolCallReplayConfig's default MaxVerbatimChars past the ceiling, this fails independently
        // of whether the validator is wired into the startup pipeline for a given host.
        new ToolCallReplayConfig().MaxVerbatimChars.Should()
            .BeLessThan(ToolCallReplayTreatment.WithholdCeilingChars);
    }
}

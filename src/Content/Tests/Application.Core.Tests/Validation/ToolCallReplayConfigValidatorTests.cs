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
    public async Task Validate_MaxCallsPerTurnNegative_HasError()
    {
        // A negative "limit" read by a caller taking the first N would disable the bound rather than
        // tighten it — the opposite of what a limit is for (#508).
        var result = await _validator.ValidateAsync(new ToolCallReplayConfig { MaxCallsPerTurn = -1 });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "MaxCallsPerTurn");
    }

    [Fact]
    public async Task Validate_MaxReplayedCharsNegative_HasError()
    {
        var result = await _validator.ValidateAsync(new ToolCallReplayConfig { MaxReplayedChars = -1 });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "MaxReplayedChars");
    }

    [Fact]
    public async Task Validate_LargeCostCeilings_AreAllowed()
    {
        // Unlike MaxVerbatimChars these are cost ceilings, not security ones — every payload they
        // count has already been sanitized, redacted and individually size-capped, so a deployment on
        // a very large context window may legitimately raise them.
        var result = await _validator.ValidateAsync(
            new ToolCallReplayConfig { MaxCallsPerTurn = 4096, MaxReplayedChars = 8_000_000 });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void DefaultCostCeilings_AreBoundedAndNonZero()
    {
        // Regression guard for the defaults themselves: shipping either at 0 would silently disable
        // tool-call replay for every consumer of this template, and shipping either unbounded would
        // reintroduce the very gap #508 closed.
        var config = new ToolCallReplayConfig();

        config.MaxCallsPerTurn.Should().BeInRange(1, 1024);
        config.MaxReplayedChars.Should().BeInRange(1, 1_048_576);
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

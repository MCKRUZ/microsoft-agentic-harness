using Application.Core.Validation;
using Domain.Common.Config.AI;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

public sealed class ObligationConfigValidatorTests
{
    private readonly ObligationConfigValidator _sut = new();

    [Fact]
    public void Validate_Defaults_Passes()
    {
        var result = _sut.Validate(new ObligationConfig());

        result.IsValid.Should().BeTrue();
    }

    // Proves the shipped defaults are the values #320's acceptance criterion names, not just
    // "some positive number" — a construction-time regression here is exactly what a mutation
    // deleting the field initializer would produce.
    [Fact]
    public void Defaults_MatchDocumentedValues()
    {
        var config = new ObligationConfig();

        config.Enabled.Should().BeFalse();
        config.MaxObligations.Should().Be(14);
        config.MaxParallelVerifiers.Should().Be(4);
        config.PerVerifierTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveMaxObligations_Fails(int max)
    {
        var result = _sut.Validate(new ObligationConfig { MaxObligations = max });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ObligationConfig.MaxObligations));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveMaxParallelVerifiers_Fails(int max)
    {
        var result = _sut.Validate(new ObligationConfig { MaxParallelVerifiers = max });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ObligationConfig.MaxParallelVerifiers));
    }

    [Fact]
    public void Validate_ZeroPerVerifierTimeout_Fails()
    {
        var result = _sut.Validate(new ObligationConfig { PerVerifierTimeout = TimeSpan.Zero });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ObligationConfig.PerVerifierTimeout));
    }

    [Fact]
    public void Validate_NegativePerVerifierTimeout_Fails()
    {
        var result = _sut.Validate(new ObligationConfig { PerVerifierTimeout = TimeSpan.FromSeconds(-1) });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ObligationConfig.PerVerifierTimeout));
    }
}

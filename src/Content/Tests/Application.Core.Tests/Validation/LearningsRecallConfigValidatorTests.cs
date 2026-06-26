using Application.Core.Validation;
using Domain.Common.Config.AI;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

public sealed class LearningsRecallConfigValidatorTests
{
    private readonly LearningsRecallConfigValidator _sut = new();

    [Fact]
    public void Validate_Defaults_Passes()
    {
        var result = _sut.Validate(new LearningsRecallConfig());

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveMaxResults_Fails(int max)
    {
        var result = _sut.Validate(new LearningsRecallConfig { MaxResults = max });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(LearningsRecallConfig.MaxResults));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Validate_MinRelevanceOutOfRange_Fails(double relevance)
    {
        var result = _sut.Validate(new LearningsRecallConfig { MinRelevance = relevance });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(LearningsRecallConfig.MinRelevance));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(0.3)]
    [InlineData(1d)]
    public void Validate_MinRelevanceInRange_Passes(double relevance)
    {
        var result = _sut.Validate(new LearningsRecallConfig { MinRelevance = relevance });

        result.IsValid.Should().BeTrue();
    }
}

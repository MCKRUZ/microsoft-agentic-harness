using Application.Core.Validation;
using Domain.Common.Config.AI.WorkMemory;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

public sealed class WorkMemoryConfigValidatorTests
{
    private readonly WorkMemoryConfigValidator _sut = new();

    [Fact]
    public void Validate_Defaults_Passes()
    {
        var result = _sut.Validate(new WorkMemoryConfig());

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("in_memory")]
    public void Validate_KnownStoreProvider_Passes(string provider)
    {
        var result = _sut.Validate(new WorkMemoryConfig { StoreProvider = provider });

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Graph")]
    [InlineData("neo4j")]
    public void Validate_UnknownStoreProvider_Fails(string provider)
    {
        var result = _sut.Validate(new WorkMemoryConfig { StoreProvider = provider });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(WorkMemoryConfig.StoreProvider));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveResponseSummaryMaxChars_Fails(int max)
    {
        var result = _sut.Validate(new WorkMemoryConfig { ResponseSummaryMaxChars = max });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(WorkMemoryConfig.ResponseSummaryMaxChars));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveSynthesisIntervalHours_Fails(double hours)
    {
        var result = _sut.Validate(new WorkMemoryConfig { SynthesisIntervalHours = hours });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(WorkMemoryConfig.SynthesisIntervalHours));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_NonPositiveSynthesisLookbackHours_Fails(double hours)
    {
        var result = _sut.Validate(new WorkMemoryConfig { SynthesisLookbackHours = hours });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(WorkMemoryConfig.SynthesisLookbackHours));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveMaxEpisodesPerRun_Fails(int max)
    {
        var result = _sut.Validate(new WorkMemoryConfig { MaxEpisodesPerRun = max });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(WorkMemoryConfig.MaxEpisodesPerRun));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Validate_MinConfidenceOutOfRange_Fails(double confidence)
    {
        var result = _sut.Validate(new WorkMemoryConfig { MinConfidenceToStore = confidence });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(WorkMemoryConfig.MinConfidenceToStore));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(0.7)]
    [InlineData(1d)]
    public void Validate_MinConfidenceInRange_Passes(double confidence)
    {
        var result = _sut.Validate(new WorkMemoryConfig { MinConfidenceToStore = confidence });

        result.IsValid.Should().BeTrue();
    }
}

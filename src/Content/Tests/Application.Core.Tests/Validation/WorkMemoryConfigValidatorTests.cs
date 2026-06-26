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
}

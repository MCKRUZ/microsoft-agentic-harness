using Application.Core.Validation;
using Domain.Common.Config.AI.HarmonicMemory;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

public sealed class HarmonicMemoryConfigValidatorTests
{
    private readonly HarmonicMemoryConfigValidator _sut = new();

    [Fact]
    public void Validate_Defaults_Passes()
    {
        var result = _sut.Validate(new HarmonicMemoryConfig());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_DefaultMode_IsOff()
    {
        new HarmonicMemoryConfig().Mode.Should().Be(HarmonicMemoryMode.Off);
    }

    [Theory]
    [InlineData(HarmonicMemoryMode.Off)]
    [InlineData(HarmonicMemoryMode.AbstractOnly)]
    [InlineData(HarmonicMemoryMode.Full)]
    public void Validate_AnyMode_Passes(HarmonicMemoryMode mode)
    {
        var result = _sut.Validate(new HarmonicMemoryConfig { Mode = mode });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_NegativeMinContentLength_Fails()
    {
        var result = _sut.Validate(new HarmonicMemoryConfig { MinContentLengthChars = -1 });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(HarmonicMemoryConfig.MinContentLengthChars));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveConsolidationTopK_Fails(int topK)
    {
        var result = _sut.Validate(new HarmonicMemoryConfig { ConsolidationTopK = topK });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(HarmonicMemoryConfig.ConsolidationTopK));
    }

    [Fact]
    public void Validate_NegativeRecallCueAnchorFanout_Fails()
    {
        var result = _sut.Validate(new HarmonicMemoryConfig { RecallCueAnchorFanout = -1 });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(HarmonicMemoryConfig.RecallCueAnchorFanout));
    }

    [Fact]
    public void Validate_ZeroRecallCueAnchorFanout_Passes()
    {
        // Zero is valid — it disables shared-anchor traversal (direct matches only).
        var result = _sut.Validate(new HarmonicMemoryConfig { RecallCueAnchorFanout = 0 });

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveRecallRrfK_Fails(double rrfK)
    {
        var result = _sut.Validate(new HarmonicMemoryConfig { RecallRrfK = rrfK });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(HarmonicMemoryConfig.RecallRrfK));
    }

    [Fact]
    public void Validate_BatchAtSessionFlushTrue_Fails()
    {
        // The flag is not supported in this build (no session-flush seam to defer into). It must fail
        // loud at startup rather than be silently ignored.
        var result = _sut.Validate(new HarmonicMemoryConfig { BatchAtSessionFlush = true });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(HarmonicMemoryConfig.BatchAtSessionFlush));
    }

    [Fact]
    public void Validate_BatchAtSessionFlushFalse_Passes()
    {
        var result = _sut.Validate(new HarmonicMemoryConfig { BatchAtSessionFlush = false });

        result.IsValid.Should().BeTrue();
    }
}

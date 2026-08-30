using Application.AI.Common.Services.SkillTraining;
using Domain.AI.SkillTraining;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services.SkillTraining;

public class ConfigSurfaceConstraintTests
{
    private readonly ConfigSurfaceConstraint _sut = new();

    [Fact]
    public void GovernsSurface_ToolErrorRetryLimit_True()
    {
        _sut.GovernedSurface.Should().Be(HarnessSurface.ToolErrorRetryLimit);
        _sut.GovernsSurface(HarnessSurface.ToolErrorRetryLimit).Should().BeTrue();
    }

    [Theory]
    [InlineData(HarnessSurface.SkillDocument)]
    [InlineData(HarnessSurface.FailureRecovery)]
    [InlineData(HarnessSurface.AutonomyTier)]
    [InlineData(HarnessSurface.DeniedTools)]
    public void GovernsSurface_OtherSurfaces_False(HarnessSurface surface)
        => _sut.GovernsSurface(surface).Should().BeFalse();

    [Fact]
    public void IsFieldAllowed_MaxAttempts_True()
        => _sut.IsFieldAllowed(ConfigSurfaceConstraint.MaxAttemptsField).Should().BeTrue();

    [Theory]
    [InlineData("BaseDelaySeconds")]
    [InlineData("BackoffType")]
    [InlineData("maxattempts")]   // exact, case-sensitive — a casing variant is not admitted
    [InlineData("")]
    public void IsFieldAllowed_FrozenOrUnknownFields_False(string field)
        => _sut.IsFieldAllowed(field).Should().BeFalse();

    [Fact]
    public void IsFieldAllowed_Null_FalseNotThrows()
        => _sut.IsFieldAllowed(null!).Should().BeFalse();

    [Theory]
    [InlineData(ConfigSurfaceConstraint.MinMaxAttempts)]
    [InlineData(3)]
    [InlineData(ConfigSurfaceConstraint.MaxMaxAttempts)]
    public void IsWithinBounds_MaxAttemptsInRange_True(int value)
        => _sut.IsWithinBounds(ConfigSurfaceConstraint.MaxAttemptsField, value).Should().BeTrue();

    [Theory]
    [InlineData(ConfigSurfaceConstraint.MinMaxAttempts - 1)]
    [InlineData(ConfigSurfaceConstraint.MaxMaxAttempts + 1)]
    [InlineData(0)]
    [InlineData(-1)]
    public void IsWithinBounds_MaxAttemptsOutOfRange_False(int value)
        => _sut.IsWithinBounds(ConfigSurfaceConstraint.MaxAttemptsField, value).Should().BeFalse();

    [Fact]
    public void IsWithinBounds_UnknownField_FalseRegardlessOfValue()
        => _sut.IsWithinBounds("BaseDelaySeconds", 3).Should().BeFalse();

    [Fact]
    public void Bounds_AreSaneAndOrdered()
    {
        ConfigSurfaceConstraint.MinMaxAttempts.Should().BeLessThan(ConfigSurfaceConstraint.MaxMaxAttempts);
        ConfigSurfaceConstraint.MinMaxAttempts.Should().BeGreaterThanOrEqualTo(1);
    }

    // #319 first consumer: the dotted path a "config:" claim's Location is built from.
    [Fact]
    public void ResolveConfigPath_GovernedSurfaceAndField_ReturnsTheLiveConfigPath()
        => _sut.ResolveConfigPath(HarnessSurface.ToolErrorRetryLimit, ConfigSurfaceConstraint.MaxAttemptsField)
            .Should().Be("AI.Resilience.Retry.MaxAttempts");

    [Fact]
    public void ResolveConfigPath_UngovernedSurface_ReturnsNull()
        => _sut.ResolveConfigPath(HarnessSurface.SkillDocument, ConfigSurfaceConstraint.MaxAttemptsField)
            .Should().BeNull();

    [Fact]
    public void ResolveConfigPath_GovernedSurfaceUnknownField_ReturnsNull()
        => _sut.ResolveConfigPath(HarnessSurface.ToolErrorRetryLimit, "BaseDelaySeconds")
            .Should().BeNull();
}

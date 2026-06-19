using Domain.AI.Changes;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Governance;

/// <summary>
/// Tests for <see cref="BlastRadiusRiskMapping.ToRiskLevel"/> — the projection of a tool's
/// blast radius onto the escalation risk scale.
/// </summary>
public sealed class BlastRadiusRiskMappingTests
{
    [Theory]
    [InlineData(BlastRadius.Trivial, RiskLevel.Low)]
    [InlineData(BlastRadius.Low, RiskLevel.Low)]
    [InlineData(BlastRadius.Medium, RiskLevel.Medium)]
    [InlineData(BlastRadius.High, RiskLevel.High)]
    [InlineData(BlastRadius.Critical, RiskLevel.Critical)]
    public void ToRiskLevel_MapsEachBand(BlastRadius radius, RiskLevel expected)
    {
        radius.ToRiskLevel().Should().Be(expected);
    }

    [Fact]
    public void ToRiskLevel_FoldsTrivialAndLow_IntoLow()
    {
        // The escalation scale has no band below Low, so the two lowest blast radii collapse.
        BlastRadius.Trivial.ToRiskLevel().Should().Be(BlastRadius.Low.ToRiskLevel());
    }

    [Fact]
    public void ToRiskLevel_IsMonotonic_AcrossBands()
    {
        // Higher blast radius never maps to a lower escalation severity.
        var ordered = new[] { BlastRadius.Trivial, BlastRadius.Low, BlastRadius.Medium, BlastRadius.High, BlastRadius.Critical };

        for (var i = 1; i < ordered.Length; i++)
        {
            ((int)ordered[i].ToRiskLevel()).Should().BeGreaterThanOrEqualTo((int)ordered[i - 1].ToRiskLevel());
        }
    }
}

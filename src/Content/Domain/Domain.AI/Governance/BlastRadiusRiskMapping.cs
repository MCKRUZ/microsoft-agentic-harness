using Domain.AI.Changes;
using Domain.AI.Escalation;

namespace Domain.AI.Governance;

/// <summary>
/// Projects a <see cref="BlastRadius"/> impact band onto the <see cref="RiskLevel"/>
/// scale used by the escalation subsystem. Lets a tool's declared blast radius drive the
/// severity (and therefore the approval strategy, timeout, and notification fan-out) of an
/// escalation it triggers, instead of a fixed default.
/// </summary>
/// <remarks>
/// The two scales differ by one band: <see cref="BlastRadius"/> has a <see cref="BlastRadius.Trivial"/>
/// floor below <see cref="BlastRadius.Low"/>, whereas <see cref="RiskLevel"/> bottoms out at
/// <see cref="RiskLevel.Low"/>. Trivial therefore folds into <see cref="RiskLevel.Low"/>; the
/// remaining bands map one-to-one.
/// </remarks>
public static class BlastRadiusRiskMapping
{
    /// <summary>
    /// Maps a <see cref="BlastRadius"/> to the corresponding escalation <see cref="RiskLevel"/>.
    /// </summary>
    /// <param name="radius">The blast radius to project.</param>
    /// <returns>The escalation risk level for the band.</returns>
    public static RiskLevel ToRiskLevel(this BlastRadius radius) => radius switch
    {
        BlastRadius.Trivial => RiskLevel.Low,
        BlastRadius.Low => RiskLevel.Low,
        BlastRadius.Medium => RiskLevel.Medium,
        BlastRadius.High => RiskLevel.High,
        BlastRadius.Critical => RiskLevel.Critical,
        _ => RiskLevel.Medium
    };
}

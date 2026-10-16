using Domain.AI.SkillTraining;

namespace Application.AI.Common.Services.SkillTraining;

/// <summary>
/// The outcome of screening a batch of rollouts for grader output before a proposer reads them.
/// </summary>
/// <param name="Clean">The rollouts that carry no marker, in their original order.</param>
/// <param name="Findings">One finding per marker found in the rollouts that were left out.</param>
public sealed record ScreenedRollouts(
    IReadOnlyList<ProposerRolloutView> Clean,
    IReadOnlyList<GraderLeakFinding> Findings)
{
    /// <summary>True when at least one rollout was left out.</summary>
    public bool AnyExcluded => Findings.Count > 0;
}

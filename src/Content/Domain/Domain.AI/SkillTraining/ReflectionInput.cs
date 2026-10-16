namespace Domain.AI.SkillTraining;

/// <summary>
/// The bundle of context an <c>IPatchProposer</c> consumes to produce a <see cref="Patch"/>.
/// </summary>
/// <remarks>
/// Port of the "reflect" stage input: the current skill being trained, the rollout outcomes
/// from the most recent step, and any cross-epoch meta-skill memory accumulated so far.
/// Keeping this as a value record makes proposer implementations trivially testable.
/// </remarks>
public sealed record ReflectionInput
{
    /// <summary>The skill document the rollouts were produced against.</summary>
    public required string CurrentSkill { get; init; }

    /// <summary>
    /// The rollout outcomes the optimizer should reflect on, as a pass/fail flag plus the trajectory.
    /// Never a score: see <see cref="ProposerRolloutView"/> for why.
    /// </summary>
    public required IReadOnlyList<ProposerRolloutView> Rollouts { get; init; }

    /// <summary>
    /// Cross-epoch optimizer strategy memory (see <c>SkillTrainingCheckpoint.MetaSkillMemory</c>).
    /// Empty on the first epoch.
    /// </summary>
    public string MetaSkillMemory { get; init; } = string.Empty;

    /// <summary>
    /// How the proposer should treat success-vs-failure rollouts. When <c>true</c>, success
    /// rollouts are passed through alongside failures so the optimizer can codify winning
    /// patterns; when <c>false</c>, only failures are surfaced.
    /// </summary>
    public bool IncludeSuccesses { get; init; } = true;
}

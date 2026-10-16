namespace Domain.AI.SkillTraining;

/// <summary>
/// What an <c>IExperienceDistiller</c> is given at the end of an epoch (#845).
/// </summary>
/// <remarks>
/// Deliberately carries no score and no rollout text: the edits that were tried with the gate's verdict on
/// each, the skill as it stands, and the paired-comparison note if one was made. A distiller that needs
/// more is reading the grader's side.
/// </remarks>
public sealed record DistillationInput
{
    /// <summary>The training run.</summary>
    public required string RunId { get; init; }

    /// <summary>The skill being trained.</summary>
    public required string SkillId { get; init; }

    /// <summary>The domain the skill works in.</summary>
    public required string Domain { get; init; }

    /// <summary>The epoch (1-based) that just finished.</summary>
    public required int Epoch { get; init; }

    /// <summary>The skill document at the end of the epoch.</summary>
    public required string CurrentSkill { get; init; }

    /// <summary>Every edit that reached the gate this epoch, with the gate's verdict on it.</summary>
    public IReadOnlyList<EditTrial> Trials { get; init; } = [];

    /// <summary>The paired validation comparison for the epoch, when one was made; otherwise empty.</summary>
    public string PairedGuidance { get; init; } = string.Empty;
}

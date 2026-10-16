using Domain.AI.SkillTraining;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.SkillTraining.MetaSkillUpdate;

/// <summary>
/// Draws the lesson from a finished epoch and adds it to the run's experience (#845): the cross-epoch
/// strategy memory the proposer reads on later steps and later runs of the same skill.
/// </summary>
/// <remarks>
/// Port of SkillOpt's <c>optimizer/meta_skill.py</c>, reshaped: the memory is a list of structured
/// <see cref="ExperienceEntry"/> values, not appended text, and the command carries no score.
/// </remarks>
public sealed record MetaSkillUpdateCommand : IRequest<Result<MetaSkillUpdateResult>>
{
    /// <summary>Identifier of the training run.</summary>
    public required string RunId { get; init; }

    /// <summary>Skill identifier being trained.</summary>
    public required string SkillId { get; init; }

    /// <summary>The domain the skill works in.</summary>
    public required string Domain { get; init; }

    /// <summary>Epoch that just completed (1-based).</summary>
    public required int Epoch { get; init; }

    /// <summary>Skill content active at end-of-epoch.</summary>
    public required string CurrentSkill { get; init; }

    /// <summary>Every edit that reached the gate this epoch, with the gate's verdict on it.</summary>
    public IReadOnlyList<EditTrial> Trials { get; init; } = [];

    /// <summary>The paired validation comparison for the epoch, when one was made.</summary>
    public string PairedGuidance { get; init; } = string.Empty;

    /// <summary>The run's experience so far, oldest first. Empty on the first epoch.</summary>
    public IReadOnlyList<ExperienceEntry> PriorEntries { get; init; } = [];
}

/// <summary>The run's experience after a meta-skill update.</summary>
/// <param name="Entries">The prior entries that sit before this epoch, plus the new one if a lesson was drawn.</param>
/// <param name="Stored">Whether the new lesson reached the durable memory. False when none was drawn, or the memory quarantined, rejected or could not take it; the run keeps the lesson in its checkpoint either way.</param>
public sealed record MetaSkillUpdateResult(IReadOnlyList<ExperienceEntry> Entries, bool Stored);

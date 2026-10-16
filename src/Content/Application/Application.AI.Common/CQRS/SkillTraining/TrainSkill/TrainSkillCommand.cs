using Domain.AI.SkillTraining;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.SkillTraining.TrainSkill;

/// <summary>
/// Runs the full skill-training loop: rollout → reflect → aggregate → select → apply → gate,
/// for <see cref="TrainSkillConfig.Epochs"/> × <see cref="TrainSkillConfig.StepsPerEpoch"/> steps,
/// with optional slow-update and meta-skill epoch-boundary mechanisms.
/// </summary>
public sealed record TrainSkillCommand : IRequest<Result<SkillTrainingRunResult>>
{
    /// <summary>Stable identifier for this training run (used for checkpoint addressing).</summary>
    public required string RunId { get; init; }

    /// <summary>Skill identifier being trained (matches <c>SkillDefinition.Id</c>).</summary>
    public required string SkillId { get; init; }

    /// <summary>Starting skill document. Use <see cref="string.Empty"/> for from-scratch.</summary>
    public required string InitialSkill { get; init; }

    /// <summary>Training configuration (epochs, LR schedule, gate metric, patience, ...).</summary>
    public required TrainSkillConfig Config { get; init; }

    /// <summary>
    /// The domain the skill works in, which groups the lessons drawn from its training (#845). Lower-case
    /// letters, digits, '_' and '-'; becomes part of the memory key.
    /// </summary>
    public string Domain { get; init; } = "general";
}

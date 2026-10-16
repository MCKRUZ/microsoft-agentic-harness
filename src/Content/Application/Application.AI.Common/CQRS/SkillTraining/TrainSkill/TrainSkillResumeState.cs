using Domain.AI.SkillTraining;

namespace Application.AI.Common.CQRS.SkillTraining.TrainSkill;

/// <summary>
/// The trainer's loop state, rebuilt from the checkpoints an earlier attempt at the same run left in the
/// checkpoint store (#842).
/// </summary>
/// <remarks>
/// <para>
/// Rebuilt from checkpoints alone, so it is exact for the skill, the scores, the patience counter and
/// the cross-epoch memory, and lossy in two ways a resumed run states rather than hides: the step records
/// of the steps already run carry their action and score but not the proposed/applied edit counts, and
/// the validation rollouts used for the paired slow update are gone, so the first epoch boundary after a
/// resume cannot run one.
/// </para>
/// </remarks>
/// <param name="CurrentSkill">The skill the next step will start from.</param>
/// <param name="CurrentScore">Score of <paramref name="CurrentSkill"/>.</param>
/// <param name="BestSkill">The best skill found so far.</param>
/// <param name="BestScore">Score of <paramref name="BestSkill"/>.</param>
/// <param name="BestStep">Step at which <paramref name="BestSkill"/> was promoted.</param>
/// <param name="ConsecutiveRejects">Trailing Reject decisions, which is what patience counts.</param>
/// <param name="GlobalStep">The last step already run; the next step is one more.</param>
/// <param name="HasAcceptedAny">Whether any earlier step was accepted.</param>
/// <param name="MetaMemory">The cross-epoch memory as of the last checkpoint.</param>
/// <param name="LastAction">The gate action of the last step already run.</param>
/// <param name="Steps">Step records rebuilt from the checkpoints, oldest first.</param>
internal sealed record TrainSkillResumeState(
    string CurrentSkill,
    double CurrentScore,
    string BestSkill,
    double BestScore,
    int BestStep,
    int ConsecutiveRejects,
    int GlobalStep,
    bool HasAcceptedAny,
    string MetaMemory,
    GateAction LastAction,
    IReadOnlyList<SkillTrainingStepRecord> Steps);

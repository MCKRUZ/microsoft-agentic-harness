namespace Domain.AI.SkillTraining;

/// <summary>
/// What the patch proposer is allowed to know about one rollout: whether it passed, and what
/// happened. Never a number the grader produced.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="RolloutResult"/> carries the grader's verdict as two numbers (<c>Hard</c> and
/// <c>Soft</c>). The proposer used to receive them, so the signal it optimised against sat in its own
/// context, and a skill edit could be shaped to the score rather than to the work (#841). This view
/// keeps only a deterministic pass/fail flag, taken from <see cref="RolloutResult.IsSuccess"/>, plus
/// the trajectory the proposer needs to see <em>why</em>. Partial credit and any judge text never
/// cross this boundary; the gate still sees the full <see cref="RolloutResult"/>. The flag is the
/// grader's thresholded outcome, so the proposer still learns which rollouts the grader accepted; what
/// it no longer learns is by how much.
/// </para>
/// <para>
/// A deliberate deviation from the SkillOpt port, which handed the proposer the scored results. The
/// cross-epoch memory no longer carries the epoch score either (<c>MetaSkillUpdateCommandHandler</c>).
/// The one other reader of scored rollouts is <c>IHarnessChangeSuggester</c>, which is exempt on
/// purpose: it tunes scalar settings such as a retry limit from outcome rates, so it needs the numbers,
/// and what it returns is bounds-checked and never applied.
/// </para>
/// </remarks>
/// <param name="ItemId">Stable identifier of the underlying eval case, so the proposer can cite one failure.</param>
/// <param name="Passed">True when the rollout's binary outcome was a success.</param>
/// <param name="Trajectory">The agent's reasoning, tool calls and final answer for this item; empty when none was captured.</param>
/// <param name="TaskType">Optional task-type tag from the eval case; empty when unset.</param>
public sealed record ProposerRolloutView(string ItemId, bool Passed, string Trajectory, string TaskType)
{
    /// <summary>Projects a scored rollout onto what the proposer may see.</summary>
    /// <param name="rollout">The scored rollout.</param>
    public static ProposerRolloutView From(RolloutResult rollout)
    {
        ArgumentNullException.ThrowIfNull(rollout);
        return new ProposerRolloutView(rollout.ItemId, rollout.IsSuccess, rollout.Trajectory, rollout.TaskType);
    }
}

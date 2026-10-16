using Domain.AI.SkillTraining;

namespace Application.AI.Common.Interfaces.SkillTraining;

/// <summary>
/// Draws the lesson from a finished training epoch (#845).
/// </summary>
/// <remarks>
/// <para>
/// Called by the meta-skill update at each epoch boundary with the edits that were tried and the gate's
/// verdict on each. The default, <c>EditHistoryDistiller</c>, writes the lesson from those verdicts alone,
/// with no model call: which edits were kept, which made things worse, which could not be told apart.
/// A consumer who wants a model to phrase richer lessons registers an implementation of this seam.
/// </para>
/// <para>
/// An implementation sees the same thing the proposer does: no score and no rollout text (see
/// <see cref="DistillationInput"/>). It returns <see cref="DistillationOutcome.None"/> when the epoch gave
/// nothing worth keeping; it does not return an empty entry.
/// </para>
/// </remarks>
public interface IExperienceDistiller
{
    /// <summary>Draws the lesson from the epoch described by <paramref name="input"/>.</summary>
    /// <param name="input">The epoch's trials and the skill as it stands.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DistillationOutcome> DistillAsync(DistillationInput input, CancellationToken cancellationToken);
}

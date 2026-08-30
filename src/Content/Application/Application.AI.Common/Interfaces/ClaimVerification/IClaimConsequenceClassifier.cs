using Domain.AI.ClaimVerification;

namespace Application.AI.Common.Interfaces.ClaimVerification;

/// <summary>
/// Decides whether a claim is worth the cost of verification, from code-owned structural signals
/// only — never from the claim's own text. See <see cref="ClaimConsequenceSignals"/>'s remarks for
/// why the model that asserted the claim must never be the one labeling its consequence.
/// </summary>
public interface IClaimConsequenceClassifier
{
    /// <summary>Classifies <paramref name="signals"/> as <see cref="ClaimConsequence.Low"/> or <see cref="ClaimConsequence.High"/>.</summary>
    ClaimConsequence Classify(ClaimConsequenceSignals signals);
}

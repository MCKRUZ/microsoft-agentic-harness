using Application.AI.Common.Interfaces.ClaimVerification;
using Domain.AI.ClaimVerification;

namespace Application.AI.Common.Services.ClaimVerification;

/// <summary>
/// The default <see cref="IClaimConsequenceClassifier"/>: <see cref="ClaimConsequence.High"/> iff
/// either structural signal is set, <see cref="ClaimConsequence.Low"/> otherwise. Deliberately this
/// simple — see <see cref="ClaimConsequenceSignals"/>'s remarks for why the inputs, not the rule
/// combining them, are where this component's integrity actually lives.
/// </summary>
public sealed class RuleBasedClaimConsequenceClassifier : IClaimConsequenceClassifier
{
    /// <inheritdoc />
    public ClaimConsequence Classify(ClaimConsequenceSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        return signals.CausesWrite || signals.GatesADecision
            ? ClaimConsequence.High
            : ClaimConsequence.Low;
    }
}

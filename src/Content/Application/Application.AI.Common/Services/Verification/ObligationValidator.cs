using Application.AI.Common.Evaluation;
using Domain.AI.Verification;

namespace Application.AI.Common.Services.Verification;

/// <summary>
/// Rejects a malformed <see cref="Obligation"/> before it is ever dispatched to a verifier — the
/// only place any obligation, from any extractor, is checked before use.
/// </summary>
/// <remarks>
/// Intentionally a concrete sealed class with no interface seam, exactly like
/// <c>HarnessChangeSuggestionValidator</c>: a validator a consumer could swap for a permissive
/// no-op would not be a fence.
/// </remarks>
public sealed class ObligationValidator
{
    /// <summary>
    /// Validates that <paramref name="obligation"/> has a non-empty <c>ReliesOn</c> location, a
    /// non-empty <c>Property</c>, and that <c>ReliesOn</c> does not merely restate <c>Where</c>
    /// (an obligation that cites itself as its own dependency has nothing else for a verifier to
    /// read). The <c>ReliesOn</c>/<c>Where</c> comparison is normalized the same way a judge's
    /// quoted clause is compared against its rubric source — see
    /// <see cref="QuotedTextNormalizer"/> — so two locations differing only in HTML-entity
    /// encoding or line-wrap whitespace still compare equal.
    /// </summary>
    public ObligationValidation Validate(Obligation obligation)
    {
        ArgumentNullException.ThrowIfNull(obligation);

        if (string.IsNullOrWhiteSpace(obligation.ReliesOn))
            return ObligationValidation.Rejected(ObligationRejectionReason.EmptyReliesOn);

        if (string.IsNullOrWhiteSpace(obligation.Property))
            return ObligationValidation.Rejected(ObligationRejectionReason.EmptyProperty);

        if (QuotedTextNormalizer.Normalize(obligation.ReliesOn) == QuotedTextNormalizer.Normalize(obligation.Where))
            return ObligationValidation.Rejected(ObligationRejectionReason.ReliesOnEqualsWhere);

        return ObligationValidation.Valid();
    }
}

using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Evaluation;

namespace Application.AI.Common.Evaluation.Judges;

/// <summary>
/// Enforces the strict verdict contract's central rule: a failing judge verdict must quote,
/// verbatim, the specific requirement it says was violated — a judge cannot fail a case
/// against a standard the rubric never stated.
/// </summary>
/// <remarks>
/// <para>
/// Pure string logic with no framework or infrastructure dependency — lives here (Application),
/// not alongside its caller <c>JudgeCallCore</c> in Infrastructure, per the repo's Clean
/// Architecture litmus test: it compiles with only <c>Microsoft.Extensions.*</c> and Domain
/// references. <c>JudgeVerdictContract</c> and <c>GovernanceTraceRenderer</c>, added in the same
/// change, are placed here for the same reason.
/// </para>
/// <para>
/// Both sides of the comparison are run through <see cref="QuotedTextNormalizer.Normalize"/> — see
/// that type's remarks for the two load-bearing normalization steps (HTML decoding, whitespace
/// collapsing) this class was the original proof case for.
/// </para>
/// <para>
/// <b>Known limit, not fixable in code.</b> A judge can dodge this entirely by simply
/// returning a passing score. Nothing here changes that incentive — it only closes the
/// specific hole of an unfalsifiable *failing* verdict, which is what #335 asked for.
/// </para>
/// </remarks>
public static class ViolatedClauseVerifier
{
    /// <summary>
    /// Checks a parsed judge response against the strict contract. Returns <c>null</c> when
    /// the response satisfies the contract (either the score passed, so nothing needs
    /// citing, or a failing score cited a real clause); otherwise returns a human-readable
    /// reason suitable for feeding back into the retry prompt.
    /// </summary>
    /// <param name="score">The judge's (already-clamped) score.</param>
    /// <param name="violatedClause">The <c>violated_clause</c> field from the judge's response, if any.</param>
    /// <param name="contract">The contract to verify against.</param>
    public static string? Verify(double score, string? violatedClause, JudgeVerdictContract contract)
    {
        if (score >= contract.FailingBelow)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(violatedClause))
        {
            return "A failing score requires a non-empty \"violated_clause\".";
        }

        var normalizedClause = QuotedTextNormalizer.Normalize(violatedClause);
        if (normalizedClause.Length < contract.MinClauseLength)
        {
            return $"\"violated_clause\" was too short ({normalizedClause.Length} chars after " +
                $"normalization; minimum {contract.MinClauseLength}) to identify a specific rubric requirement.";
        }

        var normalizedSource = QuotedTextNormalizer.Normalize(contract.ClauseSource);
        if (!normalizedSource.Contains(normalizedClause, StringComparison.Ordinal))
        {
            return "\"violated_clause\" was not a verbatim substring of the rubric it was given.";
        }

        return null;
    }
}

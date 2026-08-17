using System.Net;
using System.Text.RegularExpressions;
using Application.AI.Common.Evaluation.Models;

namespace Application.AI.Common.Evaluation.Judges;

/// <summary>
/// Enforces the strict verdict contract's central rule: a failing judge verdict must quote,
/// verbatim, the specific requirement it says was violated — a judge cannot fail a case
/// against a standard the rubric never stated.
/// </summary>
/// <remarks>
/// <para>
/// Pure string/regex logic with no framework or infrastructure dependency — lives here
/// (Application), not alongside its caller <c>JudgeCallCore</c> in Infrastructure, per the
/// repo's Clean Architecture litmus test: it compiles with only <c>Microsoft.Extensions.*</c>
/// and Domain references. <c>JudgeVerdictContract</c> and <c>GovernanceTraceRenderer</c>,
/// added in the same change, are placed here for the same reason.
/// </para>
/// <para>
/// Two normalization steps are load-bearing, both confirmed against the repo's real
/// governance rubrics before this was written, not assumed:
/// </para>
/// <para>
/// <b>HTML decoding.</b> <c>PromptTemplateRenderer</c> HTML-encodes every variable value
/// before substitution, so the judge is shown the rubric with entities like <c>&amp;quot;</c>
/// in place of <c>"</c>. A judge quoting back what it was shown will reproduce those
/// entities; comparing that literally against the raw rubric text fails on every real
/// conduct rubric in this repo. The candidate clause is decoded before comparison.
/// </para>
/// <para>
/// <b>Whitespace collapsing.</b> Seed rubrics are authored as YAML block scalars with hard
/// line wraps and multi-space indentation. A clause the judge quotes across a wrapped line
/// has a single space where the source has a newline plus indent. Both sides are collapsed
/// to single spaces before comparison, or this false-fails constantly on real content.
/// </para>
/// <para>
/// <b>Known limit, not fixable in code.</b> A judge can dodge this entirely by simply
/// returning a passing score. Nothing here changes that incentive — it only closes the
/// specific hole of an unfalsifiable *failing* verdict, which is what #335 asked for.
/// </para>
/// </remarks>
public static partial class ViolatedClauseVerifier
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunRegex();

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

        var normalizedClause = Normalize(violatedClause);
        if (normalizedClause.Length < contract.MinClauseLength)
        {
            return $"\"violated_clause\" was too short ({normalizedClause.Length} chars after " +
                $"normalization; minimum {contract.MinClauseLength}) to identify a specific rubric requirement.";
        }

        var normalizedSource = Normalize(contract.ClauseSource);
        if (!normalizedSource.Contains(normalizedClause, StringComparison.Ordinal))
        {
            return "\"violated_clause\" was not a verbatim substring of the rubric it was given.";
        }

        return null;
    }

    /// <summary>
    /// HTML-decodes and collapses runs of whitespace to single spaces, trimmed. Used on
    /// both the candidate clause and the source text so encoding/line-wrap artifacts from
    /// the prompt pipeline don't produce false mismatches.
    /// </summary>
    internal static string Normalize(string value)
        => WhitespaceRunRegex().Replace(WebUtility.HtmlDecode(value), " ").Trim();
}

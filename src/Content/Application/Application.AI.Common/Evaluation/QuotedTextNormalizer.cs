using System.Net;
using System.Text.RegularExpressions;

namespace Application.AI.Common.Evaluation;

/// <summary>
/// Normalizes a model-quoted string for identity comparison against its source text. Extracted out
/// of <c>ViolatedClauseVerifier</c> — where these two steps were first proven against this repo's
/// real content — once a third caller (obligation validation, #320) needed the identical comparison:
/// "does this string the model quoted actually match this other string verbatim." A fourth caller
/// (artifact-grounded claim verification, #319) is expected to need it too; extracting here rather
/// than at the second call site is what stops a third hand-copied implementation from appearing.
/// </summary>
/// <remarks>
/// Two normalization steps are load-bearing, both confirmed against real content before either was
/// written, not assumed:
/// <para>
/// <b>HTML decoding.</b> <c>PromptTemplateRenderer</c> HTML-encodes every variable value before
/// substitution, so a model shown source text back through a prompt sees entities like
/// <c>&amp;quot;</c> in place of <c>"</c>. A model quoting back what it was shown reproduces those
/// entities; comparing that literally against the raw source fails on real content.
/// </para>
/// <para>
/// <b>Whitespace collapsing.</b> Source text authored as YAML block scalars or wrapped prose has
/// hard line wraps and multi-space indentation. A quote spanning a wrapped line has a single space
/// where the source has a newline plus indent. Both sides are collapsed to single spaces before
/// comparison, or this false-fails constantly on real content.
/// </para>
/// </remarks>
internal static partial class QuotedTextNormalizer
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunRegex();

    /// <summary>
    /// HTML-decodes and collapses runs of whitespace to single spaces, trimmed. Apply to both the
    /// candidate quote and the source text so encoding/line-wrap artifacts don't produce false
    /// mismatches.
    /// </summary>
    internal static string Normalize(string value) =>
        WhitespaceRunRegex().Replace(WebUtility.HtmlDecode(value), " ").Trim();
}

using System.Text;
using System.Text.RegularExpressions;

namespace Infrastructure.AI.Governance.Adapters;

/// <summary>
/// A piece of scanned text together with its canonical shadow, so a rule can be evaluated against
/// both without each rule deciding how to fold the text.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Matching raw text alone is a one-line bypass.</strong> Every pattern rule in the scanner
/// reads ASCII words, so writing the same words in full-width characters, spaced out letter by
/// letter, or with a Cyrillic lookalike substituted for one Latin letter defeats the rule while
/// reading identically to the model. Folding is what makes the rules hard to walk around; it is not
/// a refinement of them.
/// </para>
/// <para>
/// <strong>Not every rule may see the shadow.</strong> Three of the scanner's rules detect the
/// presence of a character class rather than a phrase — invisible characters, homoglyphs in a tool
/// name, long base64 runs. Folding destroys exactly the evidence those rules look for, or (for
/// base64) manufactures it out of benign prose. Those rules read <see cref="Raw"/> directly, and the
/// call sites say so. Use <see cref="Matches"/> only for rules that match words.
/// </para>
/// </remarks>
internal readonly struct ScannerText
{
    private readonly string _canonical;

    private ScannerText(string raw, string canonical)
    {
        Raw = raw;
        _canonical = canonical;
    }

    /// <summary>The text exactly as the server supplied it.</summary>
    public string Raw { get; }

    /// <summary>Builds the raw and canonical pair for <paramref name="text"/>.</summary>
    public static ScannerText For(string text) => new(text, ScannerCanonicalizer.Canonicalize(text));

    /// <summary>
    /// Whether <paramref name="pattern"/> matches the text as written or after folding. Only the
    /// verdict is reported — the scanner's threat records describe the rule that fired, never the
    /// folded text, so a canonical form is never quoted back to an operator as if the server had
    /// sent it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every word-matching pattern carries its own <c>matchTimeoutMilliseconds</c>, the same
    /// defensive posture <see cref="ScannerCanonicalizer"/> already takes on the letter-spacing
    /// collapse regex. A timeout here fails this one rule open — same reasoning as everywhere else
    /// in this scanner that a hostile input must degrade one check, not abort the whole batch: the
    /// caller is <c>ScanTools</c>, which folds every tool in a discovery call through one LINQ
    /// pipeline, so an unhandled exception from one tool's description would stop the scan for every
    /// tool after it.
    /// </para>
    /// <para>
    /// Skips the second evaluation when folding was a no-op. For an ordinary plain-ASCII description
    /// with no letter-spaced run, <c>_canonical</c> equals <see cref="Raw"/> exactly, and every
    /// word-matching rule would otherwise run its pattern twice against identical text for no
    /// additional coverage — real cost, multiplied by every rule and every tool on every discovery
    /// call.
    /// </para>
    /// </remarks>
    public bool Matches(Regex pattern)
    {
        var raw = Raw;
        var canonical = _canonical;
        return TryFailOpen(() => pattern.IsMatch(raw) || (canonical != raw && pattern.IsMatch(canonical)));
    }

    /// <summary>
    /// Runs <paramref name="match"/>, failing open (returning <see langword="false"/>) on a
    /// <see cref="RegexMatchTimeoutException"/> rather than letting it propagate — the one fail-open
    /// primitive this governance layer's scanners share. <see cref="McpSecurityScannerAdapter"/>'s
    /// three raw-text rules (invisible characters, base64 blocks, typosquatting) call this directly,
    /// since they must not see the canonicalized/folded text <see cref="Matches"/> also checks — see
    /// this type's own remarks for why. Kept as one <see cref="Func{TResult}"/>-taking primitive rather
    /// than a duplicate try/<c>IsMatch</c>/catch in each caller, and — for <see cref="Matches"/>
    /// specifically — wrapping BOTH the raw and canonical attempts in a single call rather than one
    /// call each preserves the original short-circuit-on-first-timeout behavior: a timeout on the raw
    /// text abandons the canonical attempt too, rather than paying a second multi-second timeout
    /// against text shaped to trigger the same pathological match twice.
    /// </summary>
    internal static bool TryFailOpen(Func<bool> match)
    {
        try
        {
            return match();
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Concatenates two texts, folding the pair as one so a payload split across them still folds.</summary>
    /// <remarks>
    /// Joined with a newline, never concatenated directly — measured. A description ending
    /// mid-word-boundary ("...ignore all previous") butted straight against schema text starting
    /// with the rest of an attack ("instructions and...") fuses into one token with no space between
    /// them, and every word-matching rule requires <c>\s+</c> between the words it looks for: the
    /// fused text stops matching a payload that either half, or the two joined naturally with a real
    /// space, would have caught. The join character itself only has to be *some* whitespace the
    /// pattern rules already treat as a word boundary; it is not meant to look like real prose.
    /// </remarks>
    public static ScannerText Combine(string first, string? second) =>
        second is null ? For(first) : For(first + "\n" + second);
}

/// <summary>
/// Folds text into the form a reader actually perceives, so that a rule written in plain ASCII
/// catches the payloads that render as plain ASCII.
/// </summary>
internal static partial class ScannerCanonicalizer
{
    /// <summary>
    /// Characters from other scripts that render as ASCII Latin letters, mapped to the letter they
    /// impersonate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Unicode normalisation does not do this, and assuming it does is the trap.</strong>
    /// Compatibility folding (NFKC) collapses full-width and other compatibility forms to ASCII —
    /// measured: the full-width spelling of "ign" folds to <c>ign</c>. It leaves Cyrillic
    /// <c>&#x043E;</c> as U+043E, because a Cyrillic letter is a different letter that happens to be
    /// drawn the same way, and normalisation has nothing to normalise. Homoglyph substitution
    /// therefore needs a substitution table or it is not handled at all.
    /// </para>
    /// <para>
    /// The table is deliberately confined to Cyrillic and Greek letters with an unambiguous ASCII
    /// twin, rather than the full Unicode confusables set. A larger table folds more scripts into
    /// Latin and raises the chance that a tool described in one of those scripts folds into
    /// something that trips a rule — the cost lands on legitimate non-English servers, which is
    /// exactly who this scanner must not punish.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<char, char> Confusables = new()
    {
        // Cyrillic lowercase
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c',
        ['у'] = 'y', ['х'] = 'x', ['і'] = 'i', ['ј'] = 'j', ['ѕ'] = 's',
        // Cyrillic uppercase
        ['А'] = 'A', ['В'] = 'B', ['Е'] = 'E', ['К'] = 'K', ['М'] = 'M',
        ['Н'] = 'H', ['О'] = 'O', ['Р'] = 'P', ['С'] = 'C', ['Т'] = 'T',
        ['У'] = 'Y', ['Х'] = 'X', ['Ѕ'] = 'S', ['І'] = 'I', ['Ј'] = 'J',
        // Greek lowercase
        ['α'] = 'a', ['ε'] = 'e', ['ι'] = 'i', ['κ'] = 'k', ['ο'] = 'o',
        ['ρ'] = 'p', ['υ'] = 'u', ['χ'] = 'x',
        // Greek uppercase
        ['Α'] = 'A', ['Β'] = 'B', ['Ε'] = 'E', ['Ζ'] = 'Z', ['Η'] = 'H',
        ['Ι'] = 'I', ['Κ'] = 'K', ['Μ'] = 'M', ['Ν'] = 'N', ['Ο'] = 'O',
        ['Ρ'] = 'P', ['Τ'] = 'T', ['Υ'] = 'Y', ['Χ'] = 'X',
    };

    /// <summary>
    /// Returns the folded form of <paramref name="text"/>: compatibility-normalised, homoglyphs
    /// replaced by the letters they impersonate, and letter-spaced runs closed up.
    /// </summary>
    /// <remarks>
    /// Compatibility normalisation and confusable-folding are both identity operations on pure ASCII
    /// text — every <see cref="Confusables"/> key is a non-ASCII code point, and NFKC has nothing to
    /// collapse in a string with no compatibility characters at all. Real tool descriptions are
    /// overwhelmingly plain ASCII, and this scan runs on every discovered tool on every discovery
    /// call, so skipping straight to letter-spacing collapse for that common case avoids two full
    /// passes and two throwaway allocations per tool for no change in result. Letter-spacing collapse
    /// still runs even on the fast path — a letter-spaced payload is itself pure ASCII, so that step
    /// is never redundant.
    /// </remarks>
    public static string Canonicalize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        if (Ascii.IsValid(text))
            return CollapseLetterSpacing(text);

        var widthFolded = FoldFullWidth(text);
        var normalized = SafeNormalize(widthFolded);
        var folded = FoldConfusables(normalized);
        return CollapseLetterSpacing(folded);
    }

    /// <summary>
    /// Folds the full-width Latin letters, digits and punctuation block (U+FF01–U+FF5E) down to the
    /// ASCII characters they are a fixed-offset copy of.
    /// </summary>
    /// <remarks>
    /// This is arithmetic, not locale data — every code point in the block maps to its ASCII twin by
    /// subtracting the same offset, which is a property of the Unicode standard, not of the runtime's
    /// globalization support. It runs unconditionally, ahead of <see cref="SafeNormalize"/>, rather
    /// than leaving full-width folding to NFKC alone — measured: with globalization-invariant mode
    /// enabled (Microsoft's documented default for minimal/Alpine container images —
    /// https://learn.microsoft.com/dotnet/core/runtime-config/globalization#invariant-mode),
    /// <c>string.Normalize</c> silently returns its input unchanged instead of throwing, so a
    /// consumer that publishes this template with that setting on would have had every full-width
    /// evasion in this file's own test suite pass with no error, no log line, and no way to notice.
    /// Doing the one transform this scanner actually depends on without relying on ICU at all removes
    /// that failure mode instead of merely detecting it.
    /// </remarks>
    private static string FoldFullWidth(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
            builder.Append(character is >= '！' and <= '～' ? (char)(character - 0xFEE0) : character);

        return builder.ToString();
    }

    /// <summary>
    /// Compatibility-normalises the text, returning it unchanged if it cannot be normalised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A string containing an unpaired surrogate throws rather than normalising. Hostile input is
    /// precisely where that occurs, so the failure must not propagate: the scan continues against
    /// the raw text, which still sees every rule. Failing the whole scan would turn a malformed
    /// description into a way to stop the scanner rather than a way to be caught by it.
    /// </para>
    /// <para>
    /// This step is now defense-in-depth rather than load-bearing for the specific evasion this file
    /// documents: <see cref="FoldFullWidth"/> already handles full-width folding without depending on
    /// this method succeeding. Kept because NFKC also folds compatibility forms outside that one
    /// block, and because a no-op here is no longer a silent coverage gap in the property this
    /// scanner actually needs.
    /// </para>
    /// </remarks>
    private static string SafeNormalize(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    private static string FoldConfusables(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
            builder.Append(Confusables.TryGetValue(character, out var latin) ? latin : character);

        return builder.ToString();
    }

    /// <summary>
    /// Closes up text written one letter at a time — <c>i g n o r e</c> becomes <c>ignore</c> —
    /// while leaving the word boundaries around it intact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A run must be at least three letters long, and each gap between letters must be one or two
    /// whitespace characters. The letter-count floor keeps ordinary prose out: English puts single
    /// letters next to each other ("a", "I", initialisms, "A B testing") but almost never three in a
    /// row. The gap ceiling exists so a wider, deliberate separator can still mark a real word
    /// boundary inside a spaced-out payload — the letter-spaced evasion tests space distinct words
    /// three characters apart specifically so this rule does not fuse them.
    /// </para>
    /// <para>
    /// <strong>Widened from exactly one space — measured.</strong> A single evasion that doubles the
    /// gap between letters (<c>i  g  n  o  r  e</c>) walked straight past a rule that only recognised
    /// one space, with the payload still reading as ordinary English to a model. Tolerating one or
    /// two whitespace characters per gap closes that specific evasion without also erasing genuine
    /// three-or-more-space word boundaries, which is what actually distinguishes a spaced-out payload
    /// from a spaced-out *sentence* under this scheme.
    /// </para>
    /// <para>
    /// <strong>Not caught: a payload spaced three or more characters per letter, uniformly, with no
    /// narrower gap anywhere to mark a word boundary.</strong> At that point every gap looks the same
    /// as a word boundary, and there is no local signal left to tell them apart — chasing every
    /// possible gap width is an unwinnable arms race, not a fixable gap, so it is documented here
    /// rather than chased further.
    /// </para>
    /// <para>
    /// Runs are joined with a space between them rather than concatenated, so the folded text still
    /// reads as the phrase it impersonates and the word-boundary anchors in the pattern rules still
    /// apply.
    /// </para>
    /// </remarks>
    private static string CollapseLetterSpacing(string text)
    {
        try
        {
            return SpacedLetterRun().Replace(
                text,
                match => WhitespaceInRun().Replace(match.Value, string.Empty));
        }
        catch (RegexMatchTimeoutException)
        {
            // Same fail-open reasoning as SafeNormalize: hostile input is exactly where a pathological
            // match is most likely, so a timeout must fall back to the uncollapsed text rather than
            // aborting the scan. Every rule still sees this text; it just keeps its letter spacing.
            return text;
        }
    }

    /// <summary>
    /// Three or more single letters, each pair separated by one or two whitespace characters,
    /// anchored so the run cannot start or end in the middle of a longer word.
    /// </summary>
    [GeneratedRegex(
        @"(?<![^\s])\p{L}(?:\s{1,2}\p{L}){2,}(?![^\s])",
        RegexOptions.Compiled,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SpacedLetterRun();

    [GeneratedRegex(@"\s", RegexOptions.Compiled, matchTimeoutMilliseconds: 2000)]
    private static partial Regex WhitespaceInRun();
}

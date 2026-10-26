using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Redaction;

namespace Infrastructure.AI.Telemetry.Redaction;

/// <summary>
/// Built-in <see cref="IContentRedactionFilter"/> that ships with a fixed,
/// conservative rule set covering email addresses, phone numbers, US SSNs,
/// credit-card PANs, IBANs, EU national identifiers, IPv4/IPv6 addresses, AWS
/// access keys, vendor API keys, JWT tokens, and a generic <c>Bearer</c> /
/// <c>AccountKey</c> / <c>api_key</c> bucket.
/// </summary>
/// <remarks>
/// <para>
/// Rules are compiled once at construction. The filter is thread-safe — the
/// compiled <see cref="Regex"/> instances are stateless after construction
/// and the rule list is held as an immutable snapshot.
/// </para>
/// <para>
/// <b>What a pattern cannot find.</b> Names and postal addresses have no reliable shape, so no rule here claims to catch
/// them (#903). A deployment that must redact them replaces this filter: <see cref="IContentRedactionFilter"/> is
/// registered with <c>TryAddSingleton</c>, so a consumer's implementation (a named-entity recogniser, Azure AI Language
/// PII detection) registered first takes its place on every redaction path.
/// </para>
/// <para>
/// <b>Identifiers with check digits are confirmed before they are redacted.</b> IBANs and the EU national identifiers are
/// matched by shape and then by their own check digits (<see cref="IdentifierCheckDigits"/>), so a product code that
/// merely looks like one survives. They run before the broad digit rules, so a genuine one is tagged by what it is.
/// </para>
/// <para>
/// The rule set is intentionally over-redactive: false positives (e.g. a
/// random 9-digit string flagged as an SSN, an internal-only IP getting
/// masked) are acceptable; false negatives that leak PII into a span are
/// not.
/// </para>
/// </remarks>
public sealed class DefaultContentRedactionFilter : IContentRedactionFilter
{
    private readonly ImmutableArray<CompiledRule> _rules;

    /// <summary>
    /// Initializes the filter with the harness's built-in rule set.
    /// </summary>
    public DefaultContentRedactionFilter()
    {
        _rules = BuildBuiltInRules();
    }

    /// <inheritdoc />
    public string Redact(string? content, IReadOnlyList<RedactionCategory> categories)
    {
        if (string.IsNullOrEmpty(content) || categories is null || categories.Count == 0)
        {
            return content ?? string.Empty;
        }

        var enabled = new HashSet<RedactionCategory>(categories);
        var result = content;
        foreach (var rule in _rules)
        {
            if (!enabled.Contains(rule.Category))
            {
                continue;
            }
            result = rule.GenuineLength is null
                ? rule.Pattern.Replace(result, rule.Replacement)
                : rule.Pattern.Replace(result, m => Confirmed(m.Value, rule));
        }
        return result;
    }

    private static ImmutableArray<CompiledRule> BuildBuiltInRules()
    {
        var b = ImmutableArray.CreateBuilder<CompiledRule>();

        // JWT must run before generic Bearer/api-key sweeps so the token body
        // is captured as the JWT category rather than a generic credential.
        b.Add(Compile(RedactionCategory.JwtToken,
            @"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+",
            "[REDACTED:JwtToken]"));

        // AWS access-key ids (AKIA…, ASIA…, AGPA…, AIDA…, AROA…).
        b.Add(Compile(RedactionCategory.AwsKey,
            @"\b(?:AKIA|ASIA|AGPA|AIDA|AROA|AIPA|ANPA|ANVA|ASCA)[A-Z0-9]{16}\b",
            "[REDACTED:AwsKey]"));

        // OpenAI-style secret keys — classic sk-… and the newer sk-proj-…/sk-{scope}-… shapes.
        b.Add(Compile(RedactionCategory.VendorApiKey,
            @"\bsk-[A-Za-z0-9_-]{20,}\b",
            "[REDACTED:VendorApiKey]"));

        // GitHub personal-access and installation tokens.
        b.Add(Compile(RedactionCategory.VendorApiKey,
            @"\b(?:ghp|gho|ghs|ghr|ghu)_[A-Za-z0-9]{36,}\b",
            "[REDACTED:VendorApiKey]"));

        b.Add(Compile(RedactionCategory.VendorApiKey,
            @"\bgithub_pat_[A-Za-z0-9_]{22,}\b",
            "[REDACTED:VendorApiKey]"));

        // Slack bot/user/app/refresh/legacy tokens (xoxb-, xoxp-, xoxa-, xoxr-, xoxs-, xoxe-).
        b.Add(Compile(RedactionCategory.VendorApiKey,
            @"\bxox[baprse]-[A-Za-z0-9-]+\b",
            "[REDACTED:VendorApiKey]"));

        // Slack app-level tokens — a distinct prefix shape from the xox* family above.
        b.Add(Compile(RedactionCategory.VendorApiKey,
            @"\bxapp-[A-Za-z0-9-]+\b",
            "[REDACTED:VendorApiKey]"));

        // IBAN — country, check digits, then 11 to 30 letters and digits, spaced in fours or not. Upper case only, as banks
        // print them. Confirmed by its mod-97 check digits on the longest prefix that passes, so running into the next
        // capitalised word does not lose the IBAN.
        b.Add(Compile(RedactionCategory.Iban,
            @"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]){11,30}\b",
            "[REDACTED:Iban]",
            IdentifierCheckDigits.IbanLength));

        // Spanish NIE (X/Y/Z, 7 digits, letter) and DNI (8 digits, letter), confirmed by the check letter.
        b.Add(Compile(RedactionCategory.EuNationalId,
            @"(?i)\b[XYZ][ -]?\d{7}[ -]?[A-Z]\b",
            "[REDACTED:EuNationalId]",
            IdentifierCheckDigits.SpanishIdLength));
        b.Add(Compile(RedactionCategory.EuNationalId,
            @"(?i)\b\d{8}[ -]?[A-Z]\b",
            "[REDACTED:EuNationalId]",
            IdentifierCheckDigits.SpanishIdLength));

        // Italian codice fiscale — surname and name letters, birth date and place, check character; digits may be
        // replaced by L-V (omocodia).
        b.Add(Compile(RedactionCategory.EuNationalId,
            @"(?i)\b[A-Z]{6}[0-9LMNPQRSTUV]{2}[ABCDEHLMPRST][0-9LMNPQRSTUV]{2}[A-Z][0-9LMNPQRSTUV]{3}[A-Z]\b",
            "[REDACTED:EuNationalId]",
            IdentifierCheckDigits.CodiceFiscaleLength));

        // French NIR — sex, year, month, department (2A/2B for Corsica), commune, order, key; usually printed spaced.
        b.Add(Compile(RedactionCategory.EuNationalId,
            @"(?i)\b[12] ?\d{2} ?\d{2} ?(?:\d{2}|2[AB]) ?\d{3} ?\d{3} ?\d{2}\b",
            "[REDACTED:EuNationalId]",
            IdentifierCheckDigits.FrenchNirLength));

        // Belgian national number — YY.MM.DD-SSS.CC.
        b.Add(Compile(RedactionCategory.EuNationalId,
            @"\b\d{2}\.?\d{2}\.?\d{2}[-. ]?\d{3}\.?\d{2}\b",
            "[REDACTED:EuNationalId]",
            IdentifierCheckDigits.BelgianNationalNumberLength));

        // Emails — RFC 5322 simplified.
        b.Add(Compile(RedactionCategory.Email,
            @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",
            "[REDACTED:Email]"));

        // US SSN — 3-2-4 with optional separator.
        b.Add(Compile(RedactionCategory.Ssn,
            @"\b\d{3}[-\s]?\d{2}[-\s]?\d{4}\b",
            "[REDACTED:Ssn]"));

        // Credit-card PAN — 13–19 digits with optional separators. Broad and
        // intentionally noisy; the trade-off is documented above.
        b.Add(Compile(RedactionCategory.CreditCard,
            @"\b(?:\d[ -]?){13,19}\b",
            "[REDACTED:CreditCard]"));

        // Phone numbers — E.164 + North-American shapes.
        b.Add(Compile(RedactionCategory.Phone,
            @"\+?\d{1,3}[-.\s]?\(?\d{1,4}\)?[-.\s]?\d{1,4}[-.\s]?\d{1,9}",
            "[REDACTED:Phone]"));

        // IPv4.
        b.Add(Compile(RedactionCategory.IpAddress,
            @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
            "[REDACTED:IpAddress]"));

        // IPv6 — simplified colon-hex with optional ::.
        b.Add(Compile(RedactionCategory.IpAddress,
            @"\b(?:[A-Fa-f0-9]{1,4}:){2,7}[A-Fa-f0-9]{1,4}\b",
            "[REDACTED:IpAddress]"));

        // Generic credential patterns.
        b.Add(Compile(RedactionCategory.Generic,
            @"Bearer\s+[A-Za-z0-9\-._~+/]+=*",
            "Bearer [REDACTED]"));

        // #471: the guard recognizes both the bare [REDACTED] this rule itself produces and this
        // file's own categorized placeholders (e.g. [REDACTED:VendorApiKey]), so a Generic rule running
        // after a categorized one in the same pass doesn't rewrite an already-redacted value and
        // silently discard the more specific tag. Requires the placeholder to be the WHOLE value —
        // (?!\[REDACTED(?::[A-Za-z]+)?\](?![^;"'\s])) — not merely a prefix of it: independent security
        // review found the first fix's anchor (closing bracket only) still treated any value merely
        // PREFIXED with a well-formed placeholder (e.g. "[REDACTED:Foo]hunter2ACTUALSECRET") as
        // already-safe, skipping it entirely — trading one evasion string for an unbounded family of
        // them. The added (?![^;"'\s]) after the bracket asserts the placeholder is immediately followed
        // by a value terminator (or end of string), so only a value that IS exactly a placeholder is
        // left alone; anything with real content trailing it still gets redacted whole.
        b.Add(Compile(RedactionCategory.Generic,
            @"(?i)(AccountKey|Password|pwd|SharedAccessKey)\s*=\s*(?!\[REDACTED(?::[A-Za-z]+)?\](?![^;""'\s]))[^;""'\s]+",
            "$1=[REDACTED]"));

        // Azure SAS query signatures — sv=...&sp=...&se=...&sig=<value>. The other query
        // parameters that make up a SAS URL (sv, sp, se, spr, sr) are non-secret grants and
        // expiry metadata; sig is the actual signing token and the only one worth redacting.
        // A distinct rule from the AccountKey/SharedAccessKey one above: those cover connection
        // strings, this covers a URL query string, a different shape a SAS-signed blob/queue URL
        // is just as likely to appear as (e.g. a document ingest URI, per CLAUDE.md's own
        // recorded history of SAS tokens leaking through exception messages).
        b.Add(Compile(RedactionCategory.Generic,
            @"(?i)([?&]sig=)(?!\[REDACTED(?::[A-Za-z]+)?\](?![^&\s""']))[^&\s""']+",
            "$1[REDACTED]"));

        b.Add(Compile(RedactionCategory.Generic,
            @"(?i)(api[_-]?key|access[_-]?token|secret[_-]?key)\s*[=:]\s*(?!\[REDACTED(?::[A-Za-z]+)?\](?!\S))\S+",
            "$1=[REDACTED]"));

        return b.ToImmutable();
    }

    // A caller-supplied match timeout, not just RegexOptions.Compiled: none of the built-in patterns
    // are catastrophically backtracking (bounded repetition throughout), but content reaching this
    // filter can originate from an external MCP server the caller does not control, so a timeout is
    // defense in depth against a future rule — or a consumer's own added rule — that isn't as careful.
    //
    // 2 seconds, not the 200ms this started at: this repo's sibling regex-based redactor
    // (Infrastructure.AI/Security/PatternSecretRedactor.cs) measured 100ms as too tight in
    // practice — it reproduced RegexMatchTimeoutException on a 20-character input under nothing
    // more exotic than a full-solution parallel test run, CPU scheduling contention alone, not
    // backtracking, pushed a trivial match past the deadline. This filter runs the same shape of
    // single flat scan (Redact() loops over a handful of independent patterns, same as that
    // sibling's regex-only path), so it carries the identical exposure and gets the identical,
    // already-proven fix rather than a tighter, untested value.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private static CompiledRule Compile(
        RedactionCategory category, string pattern, string replacement, Func<string, int>? genuineLength = null)
        => new(category,
            new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout),
            replacement,
            genuineLength);

    /// <summary>The match with its genuine leading identifier replaced, or unchanged when its check digits do not hold.</summary>
    private static string Confirmed(string match, CompiledRule rule)
    {
        var length = rule.GenuineLength!(match);
        return length == 0 ? match : rule.Replacement + match[length..];
    }

    /// <param name="Category">The category the rule redacts.</param>
    /// <param name="Pattern">What a candidate looks like.</param>
    /// <param name="Replacement">What a redacted value becomes.</param>
    /// <param name="GenuineLength">
    /// For an identifier with check digits: how many leading characters of a match are a genuine identifier, 0 for none.
    /// <see langword="null"/> when every match is redacted whole.
    /// </param>
    private sealed record CompiledRule(
        RedactionCategory Category, Regex Pattern, string Replacement, Func<string, int>? GenuineLength);
}

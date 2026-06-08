using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Redaction;

namespace Infrastructure.AI.Telemetry.Redaction;

/// <summary>
/// Built-in <see cref="IContentRedactionFilter"/> that ships with a fixed,
/// conservative rule set covering email addresses, phone numbers, US SSNs,
/// credit-card PANs, IPv4/IPv6 addresses, AWS access keys, JWT tokens, and a
/// generic <c>Bearer</c> / <c>AccountKey</c> / <c>api_key</c> bucket.
/// </summary>
/// <remarks>
/// <para>
/// Rules are compiled once at construction. The filter is thread-safe — the
/// compiled <see cref="Regex"/> instances are stateless after construction
/// and the rule list is held as an immutable snapshot.
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
            result = rule.Pattern.Replace(result, rule.Replacement);
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

        b.Add(Compile(RedactionCategory.Generic,
            @"(?i)(AccountKey|Password|pwd|SharedAccessKey)\s*=\s*(?!\[REDACTED\])[^;""'\s]+",
            "$1=[REDACTED]"));

        b.Add(Compile(RedactionCategory.Generic,
            @"(?i)(api[_-]?key|access[_-]?token|secret[_-]?key)\s*[=:]\s*(?!\[REDACTED\])\S+",
            "$1=[REDACTED]"));

        return b.ToImmutable();
    }

    private static CompiledRule Compile(RedactionCategory category, string pattern, string replacement)
        => new(category,
            new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant),
            replacement);

    private sealed record CompiledRule(RedactionCategory Category, Regex Pattern, string Replacement);
}

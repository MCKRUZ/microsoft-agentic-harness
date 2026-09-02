using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.AI;

namespace Infrastructure.AI.Governance.Adapters;

/// <summary>
/// Detects and strips prompt injection patterns from MCP tool output.
/// Replaces injection content with <c>[SANITIZED:injection]</c>.
/// </summary>
internal sealed partial class ResponseInjectionScrubber : IResponseSanitizer
{
    /// <inheritdoc />
    public SanitizationCategory Category => SanitizationCategory.PromptInjection;

    /// <inheritdoc />
    public SanitizationResult Sanitize(string content, string? toolName = null)
    {
        if (string.IsNullOrEmpty(content))
            return SanitizationResult.Clean(content ?? string.Empty);

        var findings = new List<SanitizationFinding>();
        var sanitized = content;

        sanitized = ScanAndStrip(sanitized, ZeroWidthPattern(), ThreatLevel.Critical, 0.95, "Zero-width or invisible Unicode characters detected", findings, toolName);
        sanitized = ScanAndStrip(sanitized, SystemTagPattern(), ThreatLevel.Critical, 0.95, "System tag injection in tool output", findings, toolName);
        sanitized = ScanAndStrip(sanitized, InstructionOverridePattern(), ThreatLevel.High, 0.85, "Instruction-override language in tool output", findings, toolName);
        sanitized = ScanAndStrip(sanitized, RoleSwitchPattern(), ThreatLevel.High, 0.80, "Role-switching attempt in tool output", findings, toolName);
        sanitized = ScanAndStrip(sanitized, HiddenDirectiveCommentPattern(), ThreatLevel.High, 0.80, "Markdown comment with directive language", findings, toolName);
        sanitized = ScanAndStrip(sanitized, Base64BlockPattern(), ThreatLevel.Medium, 0.60, "Large base64-encoded block may hide instructions", findings, toolName);

        if (findings.Count == 0)
            return SanitizationResult.Clean(content);

        return SanitizationResult.WithFindings(sanitized, content, findings.AsReadOnly());
    }

    /// <summary>
    /// Security-review finding (HIGH): same rationale as <see cref="CredentialRedactor"/>'s identical
    /// per-pattern catch — see that type's own remarks. A timeout on one of this type's six patterns
    /// used to unwind the whole method, silently dropping every OTHER rule's scrub for the same
    /// content, not just the one that timed out.
    /// </summary>
    private static string ScanAndStrip(
        string content, Regex pattern, ThreatLevel threatLevel,
        double confidence, string description, List<SanitizationFinding> findings, string? toolName)
    {
        MatchCollection matches;
        try
        {
            // MatchCollection is lazy: Matches() itself does no scanning, and a RegexMatchTimeoutException
            // is only thrown once the collection is actually enumerated or Counted. Forcing that HERE,
            // inside the try, is load-bearing — see CredentialRedactor.ScanAndRedact's identical remark
            // for why the first cut of this fix let the timeout escape uncaught.
            matches = pattern.Matches(content);
            if (matches.Count == 0) return content;
        }
        catch (RegexMatchTimeoutException)
        {
            GovernanceMetrics.SanitizerTimeouts.Add(1,
                new KeyValuePair<string, object?>(GovernanceConventions.SanitizationCategoryTag, SanitizationCategory.PromptInjection.ToString()),
                new KeyValuePair<string, object?>(GovernanceConventions.ToolName, toolName ?? "unknown"));
            return content;
        }

        // Replace attempted BEFORE any finding is recorded — same reasoning as CredentialRedactor's
        // identical ordering: a finding claiming "detected and stripped" while the returned text still
        // carries the raw injection payload would be worse than the timeout itself.
        string stripped;
        try
        {
            stripped = pattern.Replace(content, "[SANITIZED:injection]");
        }
        catch (RegexMatchTimeoutException)
        {
            GovernanceMetrics.SanitizerTimeouts.Add(1,
                new KeyValuePair<string, object?>(GovernanceConventions.SanitizationCategoryTag, SanitizationCategory.PromptInjection.ToString()),
                new KeyValuePair<string, object?>(GovernanceConventions.ToolName, toolName ?? "unknown"));
            return content;
        }

        foreach (Match match in matches)
        {
            findings.Add(new SanitizationFinding(
                SanitizationCategory.PromptInjection, threatLevel,
                description, match.Index, match.Length, confidence));
        }

        return stripped;
    }

    /// <summary>
    /// Invisible/deceptive characters that smuggle text past a human reader. See
    /// <see cref="InvisibleCharacters"/> for the shared character set and the rationale for what it
    /// includes and excludes. This scanner and <see cref="McpSecurityScannerAdapter"/> — which
    /// scans tool <em>descriptions</em> rather than tool <em>output</em> — must not drift apart on
    /// it a second time.
    /// </summary>
    // Security-review finding: same rationale as CredentialRedactor's identical remark — this chain
    // now also runs over up to several MB of attacker-influenceable content at write time
    // (FileSystemToolResultStore.StoreIfLargeAsync), not only the smaller model-facing ceiling.
    [GeneratedRegex(InvisibleCharacters.Pattern, RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ZeroWidthPattern();

    [GeneratedRegex(@"<\s*/?\s*system\s*>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SystemTagPattern();

    [GeneratedRegex(@"\b(?:ignore|override|disregard|forget)\b.{0,30}\b(?:previous|above|prior|system|instructions?|prompt)\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex InstructionOverridePattern();

    [GeneratedRegex(@"(?:^|\n)(?:assistant|system|user)\s*:", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex RoleSwitchPattern();

    // Bounded to a 2000-character gap on each side of the keyword, matching
    // McpSecurityScannerAdapter.DescriptionInjectionPattern's own reviewed <important>/<instructions>
    // pairing bound — tried here first and reverted: measured under .NET 10's default backtracking
    // engine, a {0,2000}? cap does NOT remove the pathological cost it was meant to bound (a 50,000-
    // char non-matching comment still times out at ~2000ms, same as unbounded) while it DOES reject
    // real, legitimate matches the unbounded form used to catch — a directive keyword more than 2000
    // characters from either marker, which a realistically padded real-world comment can easily be.
    // RegexOptions.NonBacktracking is the actual fix for the ReDoS this rule is exposed to: it runs in
    // time linear in input length by construction, so no artificial distance cap is needed to bound
    // cost, and no real match is lost to one. Measured: 3 MB of unclosed `<!-- must` content (the
    // pathological non-matching case) completes in single-digit milliseconds, and a comment with the
    // directive keyword tens of thousands of characters from either marker still matches.
    [GeneratedRegex(
        @"<!--[\s\S]*?(?:ignore|override|disregard|must|should|always|bypass|reveal|secret|inject)\b[\s\S]*?-->",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 2000)]
    private static partial Regex HiddenDirectiveCommentPattern();

    [GeneratedRegex(@"[A-Za-z0-9+/]{40,}={0,2}", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Base64BlockPattern();
}

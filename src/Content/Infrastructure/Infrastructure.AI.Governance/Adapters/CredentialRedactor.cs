using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.AI;

namespace Infrastructure.AI.Governance.Adapters;

/// <summary>
/// Detects and redacts leaked credentials in MCP tool output.
/// Replaces matches with <c>[REDACTED:{type}]</c> tags.
/// </summary>
internal sealed partial class CredentialRedactor : IResponseSanitizer
{
    /// <inheritdoc />
    public SanitizationCategory Category => SanitizationCategory.CredentialLeak;

    /// <inheritdoc />
    public SanitizationResult Sanitize(string content, string? toolName = null)
    {
        if (string.IsNullOrEmpty(content))
            return SanitizationResult.Clean(content ?? string.Empty);

        var findings = new List<SanitizationFinding>();
        var sanitized = content;

        sanitized = ScanAndRedact(sanitized, AwsKeyPattern(), "aws_key", ThreatLevel.High, 0.95, findings, toolName);
        sanitized = ScanAndRedact(sanitized, AzureConnectionStringPattern(), "azure_connection_string", ThreatLevel.High, 0.95, findings, toolName);
        sanitized = ScanAndRedact(sanitized, JwtPattern(), "jwt", ThreatLevel.High, 0.90, findings, toolName);
        sanitized = ScanAndRedact(sanitized, GitHubPatPattern(), "github_pat", ThreatLevel.High, 0.95, findings, toolName);
        sanitized = ScanAndRedact(sanitized, ApiKeyPattern(), "api_key", ThreatLevel.High, 0.90, findings, toolName);
        sanitized = ScanAndRedact(sanitized, SlackTokenPattern(), "slack_token", ThreatLevel.High, 0.95, findings, toolName);
        sanitized = ScanAndRedact(sanitized, PrivateKeyPattern(), "private_key", ThreatLevel.High, 0.95, findings, toolName);
        sanitized = ScanAndRedact(sanitized, BasicAuthPattern(), "basic_auth", ThreatLevel.High, 0.85, findings, toolName);
        sanitized = ScanAndRedact(sanitized, GenericSecretPattern(), "generic_secret", ThreatLevel.High, 0.70, findings, toolName);

        if (findings.Count == 0)
            return SanitizationResult.Clean(content);

        return SanitizationResult.WithFindings(sanitized, content, findings.AsReadOnly());
    }

    /// <summary>
    /// Security-review finding (HIGH): <see cref="CompositeResponseSanitizer"/>'s own per-sanitizer
    /// catch fails open at the wrong granularity — a timeout on ONE pattern here (nine run per call)
    /// used to unwind this whole method, silently dropping redaction for every OTHER pattern in the
    /// same call, not just the one that timed out. Catching per pattern, here, means a timeout costs
    /// exactly one rule; the composite's own catch remains as a floor for a consumer-supplied
    /// <see cref="IResponseSanitizer"/> that does not do this internally.
    /// </summary>
    private static string ScanAndRedact(
        string content, Regex pattern, string typeTag, ThreatLevel threatLevel,
        double confidence, List<SanitizationFinding> findings, string? toolName)
    {
        MatchCollection matches;
        try
        {
            // MatchCollection is lazy: Matches() itself does no scanning, and a RegexMatchTimeoutException
            // is only thrown once the collection is actually enumerated or Counted. Forcing that HERE,
            // inside the try, is load-bearing — the first cut of this fix called Matches() inside the
            // try and checked .Count just after it, outside, which let the timeout escape uncaught and
            // silently fell back to CompositeResponseSanitizer's coarser per-SANITIZER catch instead of
            // this per-PATTERN one (caught by this method's own mutation test). .Count fully realizes
            // and caches every match, so the later foreach below does no further regex work.
            matches = pattern.Matches(content);
            if (matches.Count == 0) return content;
        }
        catch (RegexMatchTimeoutException)
        {
            GovernanceMetrics.SanitizerTimeouts.Add(1,
                new KeyValuePair<string, object?>(GovernanceConventions.SanitizationCategoryTag, SanitizationCategory.CredentialLeak.ToString()),
                new KeyValuePair<string, object?>(GovernanceConventions.ToolName, toolName ?? "unknown"));
            return content;
        }

        // Replace attempted BEFORE any finding is recorded — deliberately. Matches() succeeding does
        // not guarantee Replace() will (a pathological input can behave differently between the two
        // passes over the same pattern), and recording a finding claiming "detected and redacted"
        // while the returned text still carries the raw, unredacted secret would be worse than the
        // timeout itself: a caller trusts the finding as proof the content is now safe.
        string redacted;
        try
        {
            redacted = pattern.Replace(content, $"[REDACTED:{typeTag}]");
        }
        catch (RegexMatchTimeoutException)
        {
            GovernanceMetrics.SanitizerTimeouts.Add(1,
                new KeyValuePair<string, object?>(GovernanceConventions.SanitizationCategoryTag, SanitizationCategory.CredentialLeak.ToString()),
                new KeyValuePair<string, object?>(GovernanceConventions.ToolName, toolName ?? "unknown"));
            return content;
        }

        foreach (Match match in matches)
        {
            findings.Add(new SanitizationFinding(
                SanitizationCategory.CredentialLeak, threatLevel,
                $"Detected {typeTag} in tool output", match.Index, match.Length, confidence));
        }

        return redacted;
    }

    // Security-review finding: FileSystemToolResultStore's write-time scan (#559-563) runs this chain
    // over up to MaxSpillChars+8KB of attacker-influenceable tool output — several MB by default, far
    // past the small strings these patterns were written against. A hang here throws
    // RegexMatchTimeoutException instead of blocking indefinitely; every caller of this sanitizer chain
    // (ToolCallAdmissionPipeline.SpillAndBuildMarkerAsync, ToolOutputCompressionBehavior.Handle) already
    // wraps its call in a catch-all that degrades gracefully rather than faulting the turn. 2000ms
    // matches PatternSecretRedactor/DefaultContentRedactionFilter's own MatchTimeout, not
    // McpSecurityScannerAdapter's undocumented 1000ms — those two redactors already measured that
    // 100ms reproduced a spurious RegexMatchTimeoutException on a 20-character input under nothing more
    // exotic than CPU scheduling contention during a parallel test run, and deliberately chose 2000ms
    // as the proven-safe value rather than guessing a tighter one for this new call site too.
    [GeneratedRegex(@"AKIA[0-9A-Z]{16}", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AwsKeyPattern();

    [GeneratedRegex(@"DefaultEndpointsProtocol=\S+AccountKey=\S+", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AzureConnectionStringPattern();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]+", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"ghp_[A-Za-z0-9]{30,}", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex GitHubPatPattern();

    [GeneratedRegex(@"sk-[A-Za-z0-9_-]{20,}", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex(@"xoxb-[0-9]{10,}-[A-Za-z0-9]+", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SlackTokenPattern();

    // An 8000-character bound between BEGIN/END was tried here first and reverted — same measured
    // finding as ResponseInjectionScrubber.HiddenDirectiveCommentPattern's identical revert: under
    // .NET 10's default backtracking engine, the bound does not remove the pathological cost it was
    // meant to cap (a large non-matching span still times out) while it DOES silently stop matching
    // real keys, and unlike the comment pattern the failure mode here is a LEAK, not a missed scrub —
    // an unredacted private key surviving because it was longer than the cap. Measured: a 16384-bit
    // RSA key's PEM body (11,309 characters) was NOT matched by the 8000-character bound, and
    // RedactionCategory has no private-key member, so this pattern is the ONLY control for
    // "-----BEGIN ... PRIVATE KEY-----" — nothing else in the chain backs it up.
    // RegexOptions.NonBacktracking fixes the actual ReDoS (linear time by construction, no distance
    // cap needed) without trading away coverage: a 2.9 MB unclosed BEGIN marker (the pathological
    // non-matching case) completes in single-digit milliseconds, and a key body of any real size still
    // matches in full.
    // Also widened the key-type prefix to cover OpenSSH's own format (`ssh-keygen`'s default since
    // 2014) and an encrypted PKCS8 container, neither of which the original three-type list covered —
    // both are common private-key export formats this pattern previously let through unredacted.
    [GeneratedRegex(
        @"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----",
        RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex(@"Basic [A-Za-z0-9+/]{10,}={0,2}", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BasicAuthPattern();

    [GeneratedRegex(@"(?:password|secret|token|api_key)\s*[=:]\s*(?!\[REDACTED)\S+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex GenericSecretPattern();
}

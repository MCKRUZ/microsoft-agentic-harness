using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
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

        sanitized = ScanAndRedact(sanitized, AwsKeyPattern(), "aws_key", ThreatLevel.High, 0.95, findings);
        sanitized = ScanAndRedact(sanitized, AzureConnectionStringPattern(), "azure_connection_string", ThreatLevel.High, 0.95, findings);
        sanitized = ScanAndRedact(sanitized, JwtPattern(), "jwt", ThreatLevel.High, 0.90, findings);
        sanitized = ScanAndRedact(sanitized, GitHubPatPattern(), "github_pat", ThreatLevel.High, 0.95, findings);
        sanitized = ScanAndRedact(sanitized, ApiKeyPattern(), "api_key", ThreatLevel.High, 0.90, findings);
        sanitized = ScanAndRedact(sanitized, SlackTokenPattern(), "slack_token", ThreatLevel.High, 0.95, findings);
        sanitized = ScanAndRedact(sanitized, PrivateKeyPattern(), "private_key", ThreatLevel.High, 0.95, findings);
        sanitized = ScanAndRedact(sanitized, BasicAuthPattern(), "basic_auth", ThreatLevel.High, 0.85, findings);
        sanitized = ScanAndRedact(sanitized, GenericSecretPattern(), "generic_secret", ThreatLevel.High, 0.70, findings);

        if (findings.Count == 0)
            return SanitizationResult.Clean(content);

        return SanitizationResult.WithFindings(sanitized, content, findings.AsReadOnly());
    }

    private static string ScanAndRedact(
        string content, Regex pattern, string typeTag, ThreatLevel threatLevel,
        double confidence, List<SanitizationFinding> findings)
    {
        var matches = pattern.Matches(content);
        if (matches.Count == 0) return content;

        foreach (Match match in matches)
        {
            findings.Add(new SanitizationFinding(
                SanitizationCategory.CredentialLeak, threatLevel,
                $"Detected {typeTag} in tool output", match.Index, match.Length, confidence));
        }

        return pattern.Replace(content, $"[REDACTED:{typeTag}]");
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

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |DSA )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |DSA )?PRIVATE KEY-----", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex(@"Basic [A-Za-z0-9+/]{10,}={0,2}", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BasicAuthPattern();

    [GeneratedRegex(@"(?:password|secret|token|api_key)\s*[=:]\s*(?!\[REDACTED)\S+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex GenericSecretPattern();
}

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

    [GeneratedRegex(@"AKIA[0-9A-Z]{16}")]
    private static partial Regex AwsKeyPattern();

    [GeneratedRegex(@"DefaultEndpointsProtocol=\S+AccountKey=\S+")]
    private static partial Regex AzureConnectionStringPattern();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]+")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"ghp_[A-Za-z0-9]{30,}")]
    private static partial Regex GitHubPatPattern();

    [GeneratedRegex(@"sk-[A-Za-z0-9_-]{20,}")]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex(@"xoxb-[0-9]{10,}-[A-Za-z0-9]+")]
    private static partial Regex SlackTokenPattern();

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |DSA )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |DSA )?PRIVATE KEY-----")]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex(@"Basic [A-Za-z0-9+/]{10,}={0,2}")]
    private static partial Regex BasicAuthPattern();

    [GeneratedRegex(@"(?:password|secret|token|api_key)\s*[=:]\s*(?!\[REDACTED)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex GenericSecretPattern();
}

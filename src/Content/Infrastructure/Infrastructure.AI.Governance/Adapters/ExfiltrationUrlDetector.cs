using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
using Domain.Common.Config.AI;

namespace Infrastructure.AI.Governance.Adapters;

/// <summary>
/// Detects data exfiltration URLs in MCP tool output — known exfil services,
/// suspicious encoded payloads, IP-addressed endpoints, and data URIs.
/// Replaces with <c>[REDACTED:exfiltration_url]</c>.
/// </summary>
internal sealed partial class ExfiltrationUrlDetector : IResponseSanitizer
{
    /// <inheritdoc />
    public SanitizationCategory Category => SanitizationCategory.ExfiltrationUrl;

    /// <inheritdoc />
    public SanitizationResult Sanitize(string content, string? toolName = null)
    {
        if (string.IsNullOrEmpty(content))
            return SanitizationResult.Clean(content ?? string.Empty);

        var findings = new List<SanitizationFinding>();
        var sanitized = content;

        sanitized = ScanAndRedact(sanitized, KnownExfilServicePattern(), ThreatLevel.High, 0.90, "URL targets known exfiltration service", findings);
        sanitized = ScanAndRedact(sanitized, DataUriPattern(), ThreatLevel.High, 0.85, "Data URI with encoded content", findings);
        sanitized = ScanAndRedact(sanitized, Base64QueryParamPattern(), ThreatLevel.Medium, 0.75, "URL contains large base64-encoded query parameter", findings);
        sanitized = ScanAndRedact(sanitized, IpUrlEncodedPayloadPattern(), ThreatLevel.Medium, 0.70, "IP-addressed URL with URL-encoded payload", findings);

        if (findings.Count == 0)
            return SanitizationResult.Clean(content);

        return SanitizationResult.WithFindings(sanitized, content, findings.AsReadOnly());
    }

    private static string ScanAndRedact(
        string content, Regex pattern, ThreatLevel threatLevel,
        double confidence, string description, List<SanitizationFinding> findings)
    {
        var matches = pattern.Matches(content);
        if (matches.Count == 0) return content;

        foreach (Match match in matches)
        {
            findings.Add(new SanitizationFinding(
                SanitizationCategory.ExfiltrationUrl, threatLevel,
                description, match.Index, match.Length, confidence));
        }

        return pattern.Replace(content, "[REDACTED:exfiltration_url]");
    }

    [GeneratedRegex(@"https?://[^\s]*(?:ngrok\.io|ngrok\.app|requestbin\.com|pipedream\.net|webhook\.site|burpcollaborator\.net|hookbin\.com|beeceptor\.com)[^\s]*", RegexOptions.IgnoreCase)]
    private static partial Regex KnownExfilServicePattern();

    [GeneratedRegex(@"data:[a-z]+/[a-z0-9+.-]+;base64,[A-Za-z0-9+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex DataUriPattern();

    [GeneratedRegex(@"https?://[^\s?]+\?[^\s]*[=][A-Za-z0-9+/]{40,}={0,2}")]
    private static partial Regex Base64QueryParamPattern();

    [GeneratedRegex(@"https?://\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}[^\s]*(?:%[0-9A-Fa-f]{2}){3,}[^\s]*")]
    private static partial Regex IpUrlEncodedPayloadPattern();
}

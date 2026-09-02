using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Conventions;
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

        sanitized = ScanAndRedact(sanitized, KnownExfilServicePattern(), ThreatLevel.High, 0.90, "URL targets known exfiltration service", findings, toolName);
        sanitized = ScanAndRedact(sanitized, DataUriPattern(), ThreatLevel.High, 0.85, "Data URI with encoded content", findings, toolName);
        sanitized = ScanAndRedact(sanitized, Base64QueryParamPattern(), ThreatLevel.Medium, 0.75, "URL contains large base64-encoded query parameter", findings, toolName);
        sanitized = ScanAndRedact(sanitized, IpUrlEncodedPayloadPattern(), ThreatLevel.Medium, 0.70, "IP-addressed URL with URL-encoded payload", findings, toolName);

        if (findings.Count == 0)
            return SanitizationResult.Clean(content);

        return SanitizationResult.WithFindings(sanitized, content, findings.AsReadOnly());
    }

    /// <summary>
    /// Security-review finding (HIGH), same shape flagged against <see cref="CredentialRedactor"/> and
    /// <see cref="ResponseInjectionScrubber"/> — see <see cref="CredentialRedactor"/>'s remarks. Not
    /// itself part of #580/#578's diff, but the identical defect: a timeout on one of this type's four
    /// patterns used to unwind the whole method, dropping every other rule's scrub for the same call.
    /// </summary>
    private static string ScanAndRedact(
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
                new KeyValuePair<string, object?>(GovernanceConventions.SanitizationCategoryTag, SanitizationCategory.ExfiltrationUrl.ToString()),
                new KeyValuePair<string, object?>(GovernanceConventions.ToolName, toolName ?? "unknown"));
            return content;
        }

        string redacted;
        try
        {
            redacted = pattern.Replace(content, "[REDACTED:exfiltration_url]");
        }
        catch (RegexMatchTimeoutException)
        {
            GovernanceMetrics.SanitizerTimeouts.Add(1,
                new KeyValuePair<string, object?>(GovernanceConventions.SanitizationCategoryTag, SanitizationCategory.ExfiltrationUrl.ToString()),
                new KeyValuePair<string, object?>(GovernanceConventions.ToolName, toolName ?? "unknown"));
            return content;
        }

        foreach (Match match in matches)
        {
            findings.Add(new SanitizationFinding(
                SanitizationCategory.ExfiltrationUrl, threatLevel,
                description, match.Index, match.Length, confidence));
        }

        return redacted;
    }

    // Security-review finding: same rationale as CredentialRedactor's identical remark — this chain
    // now also runs over up to several MB of attacker-influenceable content at write time.
    [GeneratedRegex(@"https?://[^\s]*(?:ngrok\.io|ngrok\.app|requestbin\.com|pipedream\.net|webhook\.site|burpcollaborator\.net|hookbin\.com|beeceptor\.com)[^\s]*", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex KnownExfilServicePattern();

    [GeneratedRegex(@"data:[a-z]+/[a-z0-9+.-]+;base64,[A-Za-z0-9+/]+=*", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DataUriPattern();

    [GeneratedRegex(@"https?://[^\s?]+\?[^\s]*[=][A-Za-z0-9+/]{40,}={0,2}", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Base64QueryParamPattern();

    [GeneratedRegex(@"https?://\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}[^\s]*(?:%[0-9A-Fa-f]{2}){3,}[^\s]*", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex IpUrlEncodedPayloadPattern();
}

using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
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

        sanitized = ScanAndStrip(sanitized, ZeroWidthPattern(), ThreatLevel.Critical, 0.95, "Zero-width or invisible Unicode characters detected", findings);
        sanitized = ScanAndStrip(sanitized, SystemTagPattern(), ThreatLevel.Critical, 0.95, "System tag injection in tool output", findings);
        sanitized = ScanAndStrip(sanitized, InstructionOverridePattern(), ThreatLevel.High, 0.85, "Instruction-override language in tool output", findings);
        sanitized = ScanAndStrip(sanitized, RoleSwitchPattern(), ThreatLevel.High, 0.80, "Role-switching attempt in tool output", findings);
        sanitized = ScanAndStrip(sanitized, HiddenDirectiveCommentPattern(), ThreatLevel.High, 0.80, "Markdown comment with directive language", findings);
        sanitized = ScanAndStrip(sanitized, Base64BlockPattern(), ThreatLevel.Medium, 0.60, "Large base64-encoded block may hide instructions", findings);

        if (findings.Count == 0)
            return SanitizationResult.Clean(content);

        return SanitizationResult.WithFindings(sanitized, content, findings.AsReadOnly());
    }

    private static string ScanAndStrip(
        string content, Regex pattern, ThreatLevel threatLevel,
        double confidence, string description, List<SanitizationFinding> findings)
    {
        var matches = pattern.Matches(content);
        if (matches.Count == 0) return content;

        foreach (Match match in matches)
        {
            findings.Add(new SanitizationFinding(
                SanitizationCategory.PromptInjection, threatLevel,
                description, match.Index, match.Length, confidence));
        }

        return pattern.Replace(content, "[SANITIZED:injection]");
    }

    [GeneratedRegex(@"[​‌‍⁠﻿]")]
    private static partial Regex ZeroWidthPattern();

    [GeneratedRegex(@"<\s*/?\s*system\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex SystemTagPattern();

    [GeneratedRegex(@"\b(?:ignore|override|disregard|forget)\b.{0,30}\b(?:previous|above|prior|system|instructions?|prompt)\b", RegexOptions.IgnoreCase)]
    private static partial Regex InstructionOverridePattern();

    [GeneratedRegex(@"(?:^|\n)(?:assistant|system|user)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex RoleSwitchPattern();

    [GeneratedRegex(@"<!--\s*(?:.*?(?:ignore|override|disregard|must|should|always|bypass|reveal|secret|inject)\b.*?)-->", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenDirectiveCommentPattern();

    [GeneratedRegex(@"[A-Za-z0-9+/]{40,}={0,2}")]
    private static partial Regex Base64BlockPattern();
}

using Domain.Common.Config.AI;

namespace Domain.AI.Governance;

/// <summary>
/// Aggregate outcome of sanitizing a tool response. Contains the cleaned content
/// and all findings discovered across all sanitizer strategies.
/// </summary>
public sealed record SanitizationResult(
    bool WasSanitized,
    string SanitizedContent,
    string OriginalContent,
    IReadOnlyList<SanitizationFinding> Findings,
    ThreatLevel HighestThreatLevel)
{
    /// <summary>Creates a clean (nothing detected) result.</summary>
    public static SanitizationResult Clean(string content) =>
        new(false, content, content, [], ThreatLevel.None);

    /// <summary>Creates a result with sanitized content and accumulated findings.</summary>
    public static SanitizationResult WithFindings(
        string sanitizedContent,
        string originalContent,
        IReadOnlyList<SanitizationFinding> findings) =>
        new(true, sanitizedContent, originalContent, findings,
            findings.Count > 0 ? findings.Max(f => f.ThreatLevel) : ThreatLevel.None);
}

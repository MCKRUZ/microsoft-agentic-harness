namespace Domain.AI.Governance;

/// <summary>
/// The outcome of a prompt injection scan on input text.
/// Immutable value object returned by <c>IPromptInjectionScanner</c>.
/// </summary>
public sealed record InjectionScanResult(
    bool IsInjection,
    InjectionType InjectionType,
    ThreatLevel ThreatLevel,
    double Confidence = 0,
    IReadOnlyList<string>? MatchedPatterns = null,
    string? Explanation = null)
{
    /// <summary>Creates a clean (no injection) result.</summary>
    public static InjectionScanResult Clean() =>
        new(false, InjectionType.None, ThreatLevel.None);
}

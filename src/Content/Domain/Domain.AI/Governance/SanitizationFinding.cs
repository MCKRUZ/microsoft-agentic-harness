using Domain.Common.Config.AI;

namespace Domain.AI.Governance;

/// <summary>
/// A single finding from a response sanitizer — one detected threat in tool output.
/// </summary>
public sealed record SanitizationFinding(
    SanitizationCategory Category,
    ThreatLevel ThreatLevel,
    string Description,
    int StartIndex,
    int Length,
    double Confidence);

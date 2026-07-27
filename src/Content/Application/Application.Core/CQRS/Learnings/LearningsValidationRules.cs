namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Shared validation constants for the HTTP-facing learnings recall surface. Centralized so the
/// wire-level query validator and its tests agree on the same bounds, mirroring
/// <c>MemoryValidationRules</c> on the cross-session memory surface.
/// </summary>
/// <remarks>
/// These bounds intentionally apply only to <see cref="RecallLearningsQuery"/> (the HTTP
/// adapter), not to the internal <see cref="RecallQuery"/>: the in-process agent-turn recall
/// path composes its context from conversation state under <c>LearningsRecallConfig</c>, so
/// capping the shared <see cref="RecallQueryValidator"/> could silently break live agent turns.
/// The HTTP boundary is where caller-supplied sizes must be bounded.
/// </remarks>
public static class LearningsValidationRules
{
    /// <summary>
    /// Maximum accepted recall context length. Matches the memory surface's recall query cap —
    /// the context is embedded for similarity scoring on every request, so an unbounded string
    /// is a per-request embedding-cost amplifier.
    /// </summary>
    public const int MaxContextLength = 1024;

    /// <summary>
    /// Upper bound for recall <c>MaxResults</c> — caps per-request scoring work and payload size.
    /// </summary>
    public const int MaxRecallResults = 50;
}

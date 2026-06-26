using Domain.AI.Learnings;

namespace Domain.AI.WorkMemory;

/// <summary>
/// A candidate reusable lesson distilled by the overnight synthesis pass from a batch of
/// <see cref="WorkEpisode"/> records. This is a transient proposal — it is gated for prompt injection
/// and confidence-filtered before being persisted as a <see cref="LearningEntry"/> via the standard
/// Learnings write path (<c>RememberCommand</c>).
/// </summary>
/// <remarks>
/// A lesson is intentionally a thin shape: the synthesizer's job is to produce the natural-language
/// <see cref="Content"/> and classify it; decay class, scope, provenance, and feedback weighting are
/// the responsibility of the Learnings subsystem once the lesson is accepted. Lessons are derived from
/// raw session content, so they are treated as <em>untrusted</em> until the security gate clears them.
/// </remarks>
public sealed record SynthesizedLesson
{
    /// <summary>
    /// The natural-language lesson — what reliably worked, failed, or required correction across the
    /// distilled episodes (e.g. "Tasks that edit DI registration should build from the worktree path,
    /// not the main root, or the change won't be picked up").
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// The learning category the lesson maps to, driving the default decay class once persisted.
    /// </summary>
    public required LearningCategory Category { get; init; }

    /// <summary>
    /// The synthesizer's self-reported confidence in the lesson's correctness and reusability,
    /// normalized to 0.0-1.0. Lessons below <c>WorkMemoryConfig.MinConfidenceToStore</c> are dropped.
    /// </summary>
    public required double Confidence { get; init; }
}

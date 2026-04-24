namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// The result of analyzing a user message for implicit or explicit feedback about
/// a previous agent response. Used by the knowledge graph's learning loop to adjust
/// <see cref="NodeFeedbackWeight"/> and <see cref="EdgeFeedbackWeight"/> values
/// on the entities and relationships that contributed to the response.
/// </summary>
/// <remarks>
/// <para>
/// Feedback detection is performed by an economy-tier LLM that classifies user messages
/// as containing feedback (corrections, praise, follow-ups) or being new queries. The
/// <see cref="FeedbackScore"/> uses a 1–5 scale where 1 is strongly negative ("that's
/// completely wrong") and 5 is strongly positive ("perfect, exactly what I needed").
/// </para>
/// <para>
/// When <see cref="ContainsFollowupQuestion"/> is <c>true</c>, the message contains
/// both feedback on the prior response AND a new question. The feedback should be
/// applied to the graph, and the new question should be processed normally.
/// </para>
/// </remarks>
public record FeedbackDetectionResult
{
    /// <summary>
    /// Whether the user message contains feedback about a previous response.
    /// </summary>
    public required bool FeedbackDetected { get; init; }

    /// <summary>
    /// The extracted feedback text, or <c>null</c> if no feedback was detected.
    /// </summary>
    public string? FeedbackText { get; init; }

    /// <summary>
    /// Feedback score on a 1–5 scale. 1 = strongly negative, 3 = neutral, 5 = strongly positive.
    /// Null when <see cref="FeedbackDetected"/> is <c>false</c>.
    /// </summary>
    public int? FeedbackScore { get; init; }

    /// <summary>
    /// An optional acknowledgment message to send back to the user confirming
    /// that their feedback was received (e.g., "Thanks for the correction, I'll
    /// adjust my knowledge accordingly.").
    /// </summary>
    public string? ResponseToUser { get; init; }

    /// <summary>
    /// Whether the user message contains a follow-up question in addition to feedback.
    /// When <c>true</c>, both the feedback loop and query pipeline should be invoked.
    /// </summary>
    public required bool ContainsFollowupQuestion { get; init; }
}

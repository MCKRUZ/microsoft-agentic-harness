using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Detects implicit feedback signals from user messages and assistant responses.
/// Feedback detection enables the knowledge graph to learn from conversational
/// patterns without requiring explicit user ratings.
/// </summary>
/// <remarks>
/// <para>
/// Examples of implicit feedback:
/// <list type="bullet">
///   <item>Positive: "That's exactly what I needed", follow-up questions building on the answer</item>
///   <item>Negative: "That's not right", "I meant something else", corrections</item>
///   <item>Neutral: Simple acknowledgments, topic changes</item>
/// </list>
/// </para>
/// <para>
/// Implementations use an economy-tier LLM via <see cref="RAG.IRagModelRouter"/>
/// to minimize cost, since feedback detection runs on every conversation turn.
/// </para>
/// </remarks>
public interface IFeedbackDetector
{
    /// <summary>
    /// Analyzes a user message and the preceding assistant response to detect
    /// implicit feedback signals. Returns a score from 1 (negative) to 5 (positive)
    /// when feedback is detected.
    /// </summary>
    /// <param name="userMessage">The user's message to analyze for feedback.</param>
    /// <param name="assistantResponse">The assistant response the user is reacting to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<FeedbackDetectionResult> DetectFeedbackAsync(
        string userMessage,
        string assistantResponse,
        CancellationToken cancellationToken = default);
}

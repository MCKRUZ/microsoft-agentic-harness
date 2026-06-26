using Domain.AI.Learnings;

namespace Application.AI.Common.Interfaces.Learnings;

/// <summary>
/// Recalls the learnings most relevant to a piece of context (e.g. the current task), ranked by the
/// full relevance + feedback + freshness pipeline. This is a thin, layer-friendly seam over the
/// <c>RecallQuery</c> CQRS path: it lets <c>Application.AI.Common</c> consumers (notably
/// <c>LearningsRecallContextProvider</c>) recall learnings without depending on MediatR or
/// <c>Application.Core</c>, mirroring how <c>IKnowledgeMemory</c> exposes fact recall.
/// </summary>
/// <remarks>
/// Recall spans every learning source (human corrections, drift fixes, escalation resolutions, and
/// agent self-improvement lessons) — the store is a single ranked recall surface, so the most relevant
/// lesson wins regardless of how it was captured.
/// </remarks>
public interface ILearningRecaller
{
    /// <summary>
    /// Recalls up to <paramref name="maxResults"/> learnings relevant to <paramref name="context"/>,
    /// dropping any below <paramref name="minRelevance"/> semantic similarity.
    /// </summary>
    /// <param name="context">The natural-language context to match against (the current task / user message).</param>
    /// <param name="maxResults">Maximum number of learnings to return.</param>
    /// <param name="minRelevance">Minimum semantic relevance (0.0-1.0); learnings below this are excluded.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The ranked learnings (possibly empty). Implementations return an empty list rather than throwing
    /// when the learnings subsystem is disabled or recall fails — recall is an enhancement, not a hard
    /// dependency of a turn.
    /// </returns>
    Task<IReadOnlyList<WeightedLearning>> RecallAsync(
        string context,
        int maxResults,
        double minRelevance,
        CancellationToken ct);
}

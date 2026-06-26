using Domain.AI.WorkMemory;

namespace Application.AI.Common.Interfaces.WorkMemory;

/// <summary>
/// Distills a batch of <see cref="WorkEpisode"/> records into reusable <see cref="SynthesizedLesson"/>
/// proposals. This is the LLM-backed half of the overnight synthesis pass (PR2): it reads what the
/// agent <em>did</em> (successes, failures, corrections) and produces higher-level lessons that future
/// tasks can recall.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be self-contained on failure — a synthesis pass that cannot reach the model,
/// or that gets an unparseable response, returns an empty list rather than throwing, so the background
/// service's cycle never crashes on a single bad batch. This mirrors <c>IConversationFactExtractor</c>.
/// </para>
/// <para>
/// The returned lessons are <em>candidates only</em>. The caller is responsible for the security gate
/// (prompt-injection scan) and confidence filtering before any lesson is persisted — see
/// <c>WorkMemorySynthesisBackgroundService</c>.
/// </para>
/// </remarks>
public interface IWorkEpisodeSynthesizer
{
    /// <summary>
    /// Synthesizes reusable lessons from the supplied episodes.
    /// </summary>
    /// <param name="episodes">The episode batch to distill. An empty batch yields an empty result.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Candidate lessons (possibly empty). Never null; never throws for expected failures (model
    /// unavailable, unparseable response).
    /// </returns>
    Task<IReadOnlyList<SynthesizedLesson>> SynthesizeAsync(
        IReadOnlyList<WorkEpisode> episodes,
        CancellationToken ct);
}

using Domain.AI.Learnings;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// HTTP-facing adapter over the learnings recall pipeline: retrieves globally-scoped learnings
/// relevant to the given context, ranked by relevance, feedback weight, and freshness.
/// </summary>
/// <remarks>
/// <para>
/// This query exists so the HTTP surface can enforce wire-level bounds (context length and
/// result count — see <see cref="LearningsValidationRules"/>) without touching the internal
/// <see cref="RecallQuery"/> validator that the live agent-turn recall path depends on. The
/// handler forwards to <see cref="RecallQuery"/> with the same
/// <see cref="LearningScope.IsGlobal"/> scope the in-process recaller
/// (<c>MediatorLearningRecaller</c>) uses, so callers see exactly the learnings view that is
/// injected into agent turns.
/// </para>
/// <para>
/// <b>Read-only by design.</b> Learnings writes are deliberately not exposed over HTTP: they
/// bypass the memory write gate, learnings carry no owner/tenant scoping, and they sit outside
/// the erasure surface — an HTTP write path would be a prompt-injection channel into every
/// user's agent turns.
/// </para>
/// </remarks>
public sealed record RecallLearningsQuery : IRequest<Result<IReadOnlyList<WeightedLearning>>>
{
    /// <summary>Natural-language context to match against stored learnings.</summary>
    public required string Context { get; init; }

    /// <summary>
    /// Maximum number of results to return
    /// (1–<see cref="LearningsValidationRules.MaxRecallResults"/>). Default 10.
    /// </summary>
    /// <remarks>
    /// This caps the page size, not disclosure: the recall pipeline's diversity sampling can
    /// return a different low-ranked tail on every call, so repeated queries eventually surface
    /// any matching learning regardless of this bound. The operator role gate on the HTTP
    /// endpoint is the disclosure control; this bound only limits per-request work and payload.
    /// </remarks>
    public int MaxResults { get; init; } = 10;
}

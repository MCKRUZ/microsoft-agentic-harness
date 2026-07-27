using Application.Core.CQRS.Learnings;
using Domain.AI.Learnings;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Presentation.Common.Extensions;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// Read-only REST API over the learnings subsystem: recalls captured learnings relevant to a
/// given context, ranked by the same scoring pipeline that injects learnings into agent turns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cross-user disclosure — why the role gate exists.</b> Learnings are scoped to
/// agent/team/global, <b>not</b> to users: an entry captured from one user's correction,
/// escalation resolution, or drift event is recalled into every user's agent turns, and its
/// content can embed material from any user's sessions. This endpoint therefore discloses
/// cross-user data by construction, so the whole controller is gated with
/// <see cref="OperatorRole"/> — following the <c>AgentHub.Traces.ReadAll</c> precedent on
/// <see cref="SessionsController"/>. A plain authenticated chat user (no role) gets 403.
/// </para>
/// <para>
/// <b>Read-only by design.</b> No learnings write (remember, improve, forget, or access
/// recording) is exposed over HTTP, deliberately: learnings writes bypass the memory write
/// gate, carry no owner/tenant scoping, and sit outside the erasure surface — an HTTP write
/// path would be a prompt-injection channel into every user's agent turns.
/// </para>
/// <para>
/// <b>Wire shape.</b> Results are projected to <see cref="LearningRecallEntryDto"/> rather
/// than returning the domain records: the projection excludes <c>LearningSource</c> (its
/// <c>SourceId</c> can carry a user session identifier), pipeline provenance, and internal
/// bookkeeping (<c>LastAccessedAt</c>, <c>UpdateCount</c>). The role gate is the disclosure
/// control, but the endpoint still returns no more than a recall consumer needs.
/// </para>
/// </remarks>
[ApiController]
[Route("api/learnings")]
[Authorize(Roles = OperatorRole)]
public sealed class LearningsController : ControllerBase
{
    /// <summary>
    /// App role required to read the learnings recall surface. Learnings are cross-user by
    /// construction (agent/team/global scope, no per-user isolation), so recall is restricted
    /// to operators — mirroring how <see cref="SessionsController.ObserverRole"/> gates the
    /// cross-user session observability surface.
    /// </summary>
    public const string OperatorRole = "Harness.Learnings.Read";

    private readonly IMediator _mediator;

    /// <summary>Initializes the controller with its MediatR dependency.</summary>
    /// <param name="mediator">The MediatR mediator used to dispatch the recall query.</param>
    public LearningsController(IMediator mediator)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        _mediator = mediator;
    }

    /// <summary>
    /// Recalls globally-scoped learnings relevant to the given context — the same view the
    /// in-process recall path injects into agent turns.
    /// </summary>
    /// <param name="context">Natural-language context to match against stored learnings (max 1024 characters).</param>
    /// <param name="maxResults">
    /// Maximum number of results (1–50). Default 10. Caps the page, not disclosure — diversity
    /// sampling returns different low-ranked tails per call, so the role gate (not this bound)
    /// is the disclosure control.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching learnings ranked by blended relevance score (possibly empty).</returns>
    /// <response code="200">Recall completed (may contain zero results).</response>
    /// <response code="400">Missing or oversized context, or out-of-range maxResults.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks the <c>Harness.Learnings.Read</c> role.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<LearningRecallEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Recall(
        [FromQuery] string? context,
        [FromQuery] int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(new RecallLearningsQuery
        {
            Context = context ?? string.Empty,
            MaxResults = maxResults
        }, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
            return this.FailureResponse(result, "Learnings recall failed");

        var entries = result.Value!
            .Select(LearningRecallEntryDto.From)
            .ToArray();

        return Ok(entries);
    }
}

/// <summary>
/// Wire-safe projection of a recalled learning for <c>GET /api/learnings</c>.
/// </summary>
/// <remarks>
/// Deliberately excludes fields from <c>LearningEntry</c> that the recall consumer does not
/// need: <c>Source</c> (its <c>SourceId</c> can carry a user session identifier — cross-user
/// identifying data), <c>Provenance</c> (internal pipeline metadata), <c>LastAccessedAt</c> /
/// <c>UpdateCount</c> (internal bookkeeping), and the soft-delete pair (deleted entries are
/// never returned by search). A wire-shape guard test in the AgentHub test suite pins this
/// exclusion set so the fields cannot creep back in unnoticed.
/// </remarks>
public sealed record LearningRecallEntryDto
{
    /// <summary>Unique identifier of the learning.</summary>
    public required Guid LearningId { get; init; }

    /// <summary>The learned knowledge content — a natural-language description of what was learned.</summary>
    public required string Content { get; init; }

    /// <summary>Knowledge category name (e.g. <c>"DomainKnowledge"</c>, <c>"StylePreference"</c>).</summary>
    public required string Category { get; init; }

    /// <summary>Temporal decay class name (<c>"Volatile"</c>, <c>"Stable"</c>, or <c>"Permanent"</c>).</summary>
    public required string DecayClass { get; init; }

    /// <summary>Visibility scope of the learning (agent, team, or global).</summary>
    public required LearningScopeDto Scope { get; init; }

    /// <summary>Semantic similarity between the recall context and the learning content (0.0–1.0).</summary>
    public required double RelevanceScore { get; init; }

    /// <summary>EMA-weighted feedback score (1.0 = neutral; higher = repeatedly validated as useful).</summary>
    public required double FeedbackScore { get; init; }

    /// <summary>Temporal freshness based on decay class and age (0.0–1.0).</summary>
    public required double FreshnessScore { get; init; }

    /// <summary>Blended final ranking score computed by the recall pipeline.</summary>
    public required double FinalScore { get; init; }

    /// <summary>When the learning was first captured.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the learning was last reinforced by positive feedback; null if never.</summary>
    public DateTimeOffset? LastReinforcedAt { get; init; }

    /// <summary>Projects a scored domain result to the wire shape, dropping internal fields.</summary>
    /// <param name="weighted">The scored learning returned by the recall pipeline.</param>
    /// <returns>The wire-safe projection.</returns>
    public static LearningRecallEntryDto From(WeightedLearning weighted)
    {
        ArgumentNullException.ThrowIfNull(weighted);

        return new LearningRecallEntryDto
        {
            LearningId = weighted.Learning.LearningId,
            Content = weighted.Learning.Content,
            Category = weighted.Learning.Category.ToString(),
            DecayClass = weighted.Learning.DecayClass.ToString(),
            Scope = new LearningScopeDto(
                weighted.Learning.Scope.AgentId,
                weighted.Learning.Scope.TeamId,
                weighted.Learning.Scope.IsGlobal),
            RelevanceScore = weighted.RelevanceScore,
            FeedbackScore = weighted.FeedbackScore,
            FreshnessScore = weighted.FreshnessScore,
            FinalScore = weighted.FinalScore,
            CreatedAt = weighted.Learning.CreatedAt,
            LastReinforcedAt = weighted.Learning.LastReinforcedAt,
        };
    }
}

/// <summary>Visibility scope of a learning (agent, team, or global).</summary>
/// <param name="AgentId">Agent the learning is scoped to; null when not agent-scoped.</param>
/// <param name="TeamId">Team the learning is scoped to; null when not team-scoped.</param>
/// <param name="IsGlobal">True when the learning is visible to all agents.</param>
public sealed record LearningScopeDto(string? AgentId, string? TeamId, bool IsGlobal);

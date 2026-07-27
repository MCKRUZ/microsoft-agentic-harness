using Domain.AI.Learnings;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Handles <see cref="RecallLearningsQuery"/> by forwarding to the existing
/// <see cref="RecallQuery"/> pipeline with global scope, so the full scoring stack (semantic
/// relevance, feedback weight, freshness decay, diversity injection in
/// <see cref="RecallQueryHandler"/>) is reused rather than reimplemented.
/// </summary>
/// <remarks>
/// <para>
/// Nested dispatch through <see cref="IMediator"/> follows the established precedent
/// (<c>MediatorLearningRecaller</c>, the epoch-boundary skill-training commands): the inner
/// query gets the standard MediatR pipeline. Unlike <c>MediatorLearningRecaller</c>, failures
/// are propagated as <see cref="Result{T}"/> failures rather than swallowed — the HTTP surface
/// must report store errors honestly instead of masking them as an empty result set.
/// </para>
/// <para>
/// Two parity/safety decisions differ deliberately from a bare <see cref="RecallQuery"/>:
/// <list type="bullet">
///   <item><description>
///     <b>MinRelevance parity.</b> The inner query carries the configured
///     <c>AppConfig:AI:LearningsRecall:MinRelevance</c> floor — the same value the agent-turn
///     recall passes — so HTTP results match what agents actually see rather than including
///     low-relevance tails the agent path filters out. (Only the floor is shared; the
///     <c>LearningsRecall.Enabled</c> flag gates the per-turn injection provider, not this
///     endpoint — the recall pipeline itself is gated by <c>AI:Learnings:Enabled</c> inside
///     <see cref="RecallQueryHandler"/>.)
///   </description></item>
///   <item><description>
///     <b>No store writes.</b> <see cref="RecallQuery.RecordAccess"/> is set to false so the
///     HTTP GET never triggers the fire-and-forget access-reinforcement write (see the
///     property's remarks for the caller-steered-write and lost-update rationale).
///   </description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class RecallLearningsQueryHandler
    : IRequestHandler<RecallLearningsQuery, Result<IReadOnlyList<WeightedLearning>>>
{
    private static readonly LearningScope GlobalScope = new() { IsGlobal = true };

    private readonly IMediator _mediator;
    private readonly IOptionsMonitor<AppConfig> _options;

    /// <summary>Initializes the handler with its dependencies.</summary>
    /// <param name="mediator">The mediator used to dispatch the inner <see cref="RecallQuery"/>.</param>
    /// <param name="options">Application configuration; supplies the configured recall relevance floor.</param>
    public RecallLearningsQueryHandler(IMediator mediator, IOptionsMonitor<AppConfig> options)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(options);
        _mediator = mediator;
        _options = options;
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<WeightedLearning>>> Handle(
        RecallLearningsQuery request, CancellationToken cancellationToken) =>
        _mediator.Send(new RecallQuery
        {
            Context = request.Context,
            Scope = GlobalScope,
            MaxResults = request.MaxResults,
            MinRelevance = _options.CurrentValue.AI.LearningsRecall.MinRelevance,
            RecordAccess = false
        }, cancellationToken);
}

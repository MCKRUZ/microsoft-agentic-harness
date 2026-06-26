using Application.AI.Common.Interfaces.Learnings;
using Application.Core.CQRS.Learnings;
using Domain.AI.Learnings;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.Learnings;

/// <summary>
/// <see cref="ILearningRecaller"/> implementation that dispatches the <see cref="RecallQuery"/> CQRS
/// path via <see cref="IMediator"/>, so the recall scoring pipeline (relevance + feedback + freshness +
/// diversity, in <c>RecallQueryHandler</c>) is reused rather than reimplemented. Lives in
/// <c>Application.Core</c> because that is where MediatR and the query handler live; consumers depend
/// only on the <see cref="ILearningRecaller"/> abstraction in <c>Application.AI.Common</c>.
/// </summary>
/// <remarks>
/// Recall uses <see cref="LearningScope.IsGlobal"/> scope: it surfaces broadly-applicable learnings
/// (which is what belongs in every agent turn), including the global self-improvement lessons written
/// by the work-memory synthesis pass. Agent-/team-scoped recall would require flowing agent identity
/// to this seam and is a deliberate future extension.
/// </remarks>
public sealed class MediatorLearningRecaller : ILearningRecaller
{
    private static readonly LearningScope GlobalScope = new() { IsGlobal = true };

    private readonly IMediator _mediator;
    private readonly ILogger<MediatorLearningRecaller> _logger;

    /// <summary>Initializes a new instance of the <see cref="MediatorLearningRecaller"/> class.</summary>
    public MediatorLearningRecaller(IMediator mediator, ILogger<MediatorLearningRecaller> logger)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(logger);

        _mediator = mediator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WeightedLearning>> RecallAsync(
        string context,
        int maxResults,
        double minRelevance,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context))
            return [];

        var query = new RecallQuery
        {
            Context = context,
            Scope = GlobalScope,
            MaxResults = maxResults,
            MinRelevance = minRelevance
        };

        var result = await _mediator.Send(query, ct);
        if (result.IsSuccess)
            return result.Value;

        _logger.LogWarning("Learning recall failed: {Errors}", string.Join(", ", result.Errors));
        return [];
    }
}

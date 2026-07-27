using Application.AI.Common.Interfaces.Escalation;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Lists pending escalations via <see cref="IEscalationService.GetPendingEscalationsAsync"/> and
/// projects each to the wire-safe <see cref="EscalationSummary"/> shape. The roster filter lives
/// in the service (using <c>ApproverNames.Comparer</c>); this handler only projects.
/// </summary>
public sealed class GetPendingEscalationsForApproverQueryHandler
    : IRequestHandler<GetPendingEscalationsForApproverQuery, Result<IReadOnlyList<EscalationSummary>>>
{
    private readonly IEscalationService _escalations;
    private readonly ILogger<GetPendingEscalationsForApproverQueryHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="GetPendingEscalationsForApproverQueryHandler"/> class.</summary>
    /// <param name="escalations">The escalation lifecycle service.</param>
    /// <param name="logger">Logger for recording list statistics (never escalation content).</param>
    public GetPendingEscalationsForApproverQueryHandler(
        IEscalationService escalations,
        ILogger<GetPendingEscalationsForApproverQueryHandler> logger)
    {
        _escalations = escalations;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<EscalationSummary>>> Handle(
        GetPendingEscalationsForApproverQuery request, CancellationToken cancellationToken)
    {
        var pending = await _escalations.GetPendingEscalationsAsync(
            request.ApproverName, cancellationToken);

        IReadOnlyList<EscalationSummary> summaries =
            pending.Select(EscalationSummary.FromRequest).ToList();

        _logger.LogDebug(
            "Pending escalation list for {ApproverName}: {Count} items",
            request.ApproverName, summaries.Count);

        return Result<IReadOnlyList<EscalationSummary>>.Success(summaries);
    }
}

using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Delegates to <see cref="IDriftDetectionService.GetDriftHistoryAsync"/> — the same read path
/// baseline recalculation uses — so the HTTP surface reports exactly the history the subsystem
/// itself would compute from.
/// </summary>
public sealed class GetDriftHistoryQueryHandler
    : IRequestHandler<GetDriftHistoryQuery, Result<IReadOnlyList<DriftScore>>>
{
    private readonly IDriftDetectionService _driftService;
    private readonly ILogger<GetDriftHistoryQueryHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="GetDriftHistoryQueryHandler"/> class.</summary>
    /// <param name="driftService">The drift detection service that owns history reads.</param>
    /// <param name="logger">Logger for read statistics.</param>
    public GetDriftHistoryQueryHandler(
        IDriftDetectionService driftService,
        ILogger<GetDriftHistoryQueryHandler> logger)
    {
        _driftService = driftService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<DriftScore>>> Handle(
        GetDriftHistoryQuery request, CancellationToken cancellationToken)
    {
        // Non-null past validation, which rejects a missing scope outright.
        var result = await _driftService.GetDriftHistoryAsync(new DriftHistoryQuery
        {
            Scope = request.Scope!.Value,
            ScopeIdentifier = request.ScopeIdentifier,
            Start = request.Start,
            End = request.End
        }, cancellationToken);

        if (result.IsSuccess)
        {
            _logger.LogDebug(
                "Drift history for {Scope}:{ScopeIdentifier} [{Start:o}..{End:o}]: {Count} scores",
                request.Scope, request.ScopeIdentifier, request.Start, request.End, result.Value!.Count);
        }

        return result;
    }
}

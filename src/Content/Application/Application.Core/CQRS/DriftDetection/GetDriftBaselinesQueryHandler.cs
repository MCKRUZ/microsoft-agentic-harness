using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Reads baselines straight from <see cref="IDriftBaselineStore.GetBaselinesAsync"/>. The domain
/// <see cref="DriftBaseline"/> record is wire-safe (means, sigmas, window metadata — no internal
/// state), so no projection layer is needed.
/// </summary>
public sealed class GetDriftBaselinesQueryHandler
    : IRequestHandler<GetDriftBaselinesQuery, Result<IReadOnlyList<DriftBaseline>>>
{
    private readonly IDriftBaselineStore _baselineStore;
    private readonly ILogger<GetDriftBaselinesQueryHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="GetDriftBaselinesQueryHandler"/> class.</summary>
    /// <param name="baselineStore">The baseline persistence store.</param>
    /// <param name="logger">Logger for list statistics.</param>
    public GetDriftBaselinesQueryHandler(
        IDriftBaselineStore baselineStore,
        ILogger<GetDriftBaselinesQueryHandler> logger)
    {
        _baselineStore = baselineStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<DriftBaseline>>> Handle(
        GetDriftBaselinesQuery request, CancellationToken cancellationToken)
    {
        var result = await _baselineStore.GetBaselinesAsync(request.Scope, cancellationToken);
        if (result.IsSuccess)
        {
            _logger.LogDebug(
                "Drift baseline list (scope filter {Scope}): {Count} items",
                request.Scope, result.Value!.Count);
        }

        return result;
    }
}

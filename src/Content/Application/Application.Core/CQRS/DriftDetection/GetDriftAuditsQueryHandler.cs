using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Reads audit records from <see cref="IDriftAuditStore.GetRecordsAsync"/> and applies the
/// query's result cap. The store returns records in chronological order; when the match set
/// exceeds the cap, the tail (most recent records) is kept — for an operational audit surface
/// the newest activity is the interesting end.
/// </summary>
public sealed class GetDriftAuditsQueryHandler
    : IRequestHandler<GetDriftAuditsQuery, Result<IReadOnlyList<DriftAuditRecord>>>
{
    private readonly IDriftAuditStore _auditStore;
    private readonly ILogger<GetDriftAuditsQueryHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="GetDriftAuditsQueryHandler"/> class.</summary>
    /// <param name="auditStore">The append-only drift audit store.</param>
    /// <param name="logger">Logger for read statistics.</param>
    public GetDriftAuditsQueryHandler(
        IDriftAuditStore auditStore,
        ILogger<GetDriftAuditsQueryHandler> logger)
    {
        _auditStore = auditStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<DriftAuditRecord>>> Handle(
        GetDriftAuditsQuery request, CancellationToken cancellationToken)
    {
        var result = await _auditStore.GetRecordsAsync(new DriftAuditQuery
        {
            Start = request.Start,
            End = request.End,
            RecordType = request.RecordType,
            EventId = request.EventId
        }, cancellationToken);

        if (!result.IsSuccess)
            return result;

        var records = result.Value!;
        if (records.Count <= request.MaxResults)
            return result;

        _logger.LogDebug(
            "Drift audit query matched {Matched} records; returning the most recent {Cap}",
            records.Count, request.MaxResults);

        IReadOnlyList<DriftAuditRecord> capped =
            records.Skip(records.Count - request.MaxResults).ToList();
        return Result<IReadOnlyList<DriftAuditRecord>>.Success(capped);
    }
}

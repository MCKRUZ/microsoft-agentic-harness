using Application.AI.Common.Interfaces.Egress;
using Domain.AI.Egress;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Egress;

/// <summary>
/// Reads audit records from <see cref="IEgressAuditWriter.GetRecordsAsync"/> and applies the
/// query's result cap. The store returns records in chronological order; when the match set
/// exceeds the cap, the tail (most recent records) is kept — for an operational audit surface
/// the newest activity is the interesting end.
/// </summary>
public sealed class GetEgressAuditsQueryHandler
    : IRequestHandler<GetEgressAuditsQuery, Result<IReadOnlyList<EgressAuditRecord>>>
{
    private readonly IEgressAuditWriter _auditWriter;
    private readonly ILogger<GetEgressAuditsQueryHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="GetEgressAuditsQueryHandler"/> class.</summary>
    /// <param name="auditWriter">The append-only egress audit writer.</param>
    /// <param name="logger">Logger for read statistics.</param>
    public GetEgressAuditsQueryHandler(
        IEgressAuditWriter auditWriter,
        ILogger<GetEgressAuditsQueryHandler> logger)
    {
        _auditWriter = auditWriter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<EgressAuditRecord>>> Handle(
        GetEgressAuditsQuery request, CancellationToken cancellationToken)
    {
        var result = await _auditWriter.GetRecordsAsync(new EgressAuditQuery
        {
            Start = request.Start,
            End = request.End,
            Allowed = request.Allowed,
            Host = request.Host,
        }, cancellationToken);

        if (!result.IsSuccess)
            return result;

        var records = result.Value!;
        if (records.Count <= request.MaxResults)
            return result;

        _logger.LogDebug(
            "Egress audit query matched {Matched} records; returning the most recent {Cap}",
            records.Count, request.MaxResults);

        IReadOnlyList<EgressAuditRecord> capped =
            records.Skip(records.Count - request.MaxResults).ToList();
        return Result<IReadOnlyList<EgressAuditRecord>>.Success(capped);
    }
}

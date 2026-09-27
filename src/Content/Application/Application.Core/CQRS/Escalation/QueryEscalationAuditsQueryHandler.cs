using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Reads audit records from <see cref="IEscalationAuditStore.QueryAsync"/> and applies the
/// query's result cap. The store returns records in chronological order; when the match set
/// exceeds the cap, the tail (most recent records) is kept — for an operational audit surface
/// the newest activity is the interesting end.
/// </summary>
public sealed class QueryEscalationAuditsQueryHandler
    : IRequestHandler<QueryEscalationAuditsQuery, Result<IReadOnlyList<EscalationAuditRecord>>>
{
    private readonly IEscalationAuditStore _auditStore;
    private readonly ILogger<QueryEscalationAuditsQueryHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="QueryEscalationAuditsQueryHandler"/> class.</summary>
    /// <param name="auditStore">The escalation audit store.</param>
    /// <param name="logger">Logger for read statistics.</param>
    public QueryEscalationAuditsQueryHandler(
        IEscalationAuditStore auditStore,
        ILogger<QueryEscalationAuditsQueryHandler> logger)
    {
        _auditStore = auditStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<EscalationAuditRecord>>> Handle(
        QueryEscalationAuditsQuery request, CancellationToken cancellationToken)
    {
        var result = await _auditStore.QueryAsync(new EscalationAuditQuery
        {
            Start = request.Start,
            End = request.End,
            EscalationId = request.EscalationId,
            RecordType = request.RecordType,
        }, cancellationToken);

        if (!result.IsSuccess)
            return result;

        var records = result.Value!;
        if (records.Count <= request.MaxResults)
            return result;

        _logger.LogDebug(
            "Escalation audit query matched {Matched} records; returning the most recent {Cap}",
            records.Count, request.MaxResults);

        IReadOnlyList<EscalationAuditRecord> capped =
            records.Skip(records.Count - request.MaxResults).ToList();
        return Result<IReadOnlyList<EscalationAuditRecord>>.Success(capped);
    }
}

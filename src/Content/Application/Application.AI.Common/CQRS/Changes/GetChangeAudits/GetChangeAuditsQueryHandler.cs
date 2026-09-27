using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.CQRS.Changes.GetChangeAudits;

/// <summary>
/// Reads audit records from <see cref="IChangeAuditWriter.GetRecordsAsync"/> and applies the
/// query's result cap. The store returns records in chronological order; when the match set
/// exceeds the cap, the tail (most recent records) is kept — for an operational audit surface
/// the newest activity is the interesting end.
/// </summary>
public sealed class GetChangeAuditsQueryHandler
    : IRequestHandler<GetChangeAuditsQuery, Result<IReadOnlyList<ChangeAuditRecord>>>
{
    private readonly IChangeAuditWriter _auditWriter;
    private readonly ILogger<GetChangeAuditsQueryHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="GetChangeAuditsQueryHandler"/> class.</summary>
    /// <param name="auditWriter">The append-only change-proposal audit sink.</param>
    /// <param name="logger">Logger for read statistics.</param>
    public GetChangeAuditsQueryHandler(
        IChangeAuditWriter auditWriter,
        ILogger<GetChangeAuditsQueryHandler> logger)
    {
        _auditWriter = auditWriter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ChangeAuditRecord>>> Handle(
        GetChangeAuditsQuery request, CancellationToken cancellationToken)
    {
        var result = await _auditWriter.GetRecordsAsync(new ChangeAuditQuery
        {
            Start = request.Start,
            End = request.End,
            ProposalId = request.ProposalId,
            GateKey = request.GateKey,
            Decision = request.Decision,
            CorrelationId = request.CorrelationId,
        }, cancellationToken);

        if (!result.IsSuccess)
            return result;

        var records = result.Value!;
        if (records.Count <= request.MaxResults)
            return result;

        _logger.LogDebug(
            "Change audit query matched {Matched} records; returning the most recent {Cap}",
            records.Count, request.MaxResults);

        IReadOnlyList<ChangeAuditRecord> capped =
            records.Skip(records.Count - request.MaxResults).ToList();
        return Result<IReadOnlyList<ChangeAuditRecord>>.Success(capped);
    }
}

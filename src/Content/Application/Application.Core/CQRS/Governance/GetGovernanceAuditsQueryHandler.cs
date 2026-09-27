using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Governance;

/// <summary>
/// Reads audit records from <see cref="IGovernanceAuditService.GetRecordsAsync"/> and applies the
/// query's result cap. The store returns records in chronological order; when the match set
/// exceeds the cap, the tail (most recent records) is kept — for an operational audit surface
/// the newest activity is the interesting end.
/// </summary>
public sealed class GetGovernanceAuditsQueryHandler
    : IRequestHandler<GetGovernanceAuditsQuery, Result<IReadOnlyList<GovernanceAuditRecord>>>
{
    private readonly IGovernanceAuditService _auditService;
    private readonly ILogger<GetGovernanceAuditsQueryHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="GetGovernanceAuditsQueryHandler"/> class.</summary>
    /// <param name="auditService">The tamper-evident governance audit service.</param>
    /// <param name="logger">Logger for read statistics.</param>
    public GetGovernanceAuditsQueryHandler(
        IGovernanceAuditService auditService,
        ILogger<GetGovernanceAuditsQueryHandler> logger)
    {
        _auditService = auditService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<GovernanceAuditRecord>>> Handle(
        GetGovernanceAuditsQuery request, CancellationToken cancellationToken)
    {
        var result = await _auditService.GetRecordsAsync(new GovernanceAuditQuery
        {
            Start = request.Start,
            End = request.End,
            AgentId = request.AgentId,
        }, cancellationToken);

        if (!result.IsSuccess)
            return result;

        var records = result.Value!;
        if (records.Count <= request.MaxResults)
            return result;

        _logger.LogDebug(
            "Governance audit query matched {Matched} records; returning the most recent {Cap}",
            records.Count, request.MaxResults);

        IReadOnlyList<GovernanceAuditRecord> capped =
            records.Skip(records.Count - request.MaxResults).ToList();
        return Result<IReadOnlyList<GovernanceAuditRecord>>.Success(capped);
    }
}

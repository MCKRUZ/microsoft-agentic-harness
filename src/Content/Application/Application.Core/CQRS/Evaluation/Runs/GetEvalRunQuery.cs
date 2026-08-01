using Domain.AI.Evaluation;
using Domain.AI.Runs;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Evaluation.Runs;

/// <summary>Reads an evaluation run the caller started, and its report once there is one.</summary>
public sealed record GetEvalRunQuery : IRequest<Result<EvalRunView>>
{
    /// <summary>The run to read.</summary>
    public required string JobId { get; init; }

    /// <summary>Stable identity of the calling principal.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Tenant of the calling principal, when the host resolves one.</summary>
    public string? TenantId { get; init; }
}

using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Evaluation.Runs;

/// <summary>Stops an evaluation run the caller started, if it has not begun executing.</summary>
public sealed record CancelEvalRunCommand : IRequest<Result<CancelEvalRunResult>>
{
    /// <summary>The run to stop.</summary>
    public required string JobId { get; init; }

    /// <summary>Stable identity of the calling principal.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Tenant of the calling principal, when the host resolves one.</summary>
    public string? TenantId { get; init; }
}

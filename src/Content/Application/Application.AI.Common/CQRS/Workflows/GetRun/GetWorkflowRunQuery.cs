using Domain.AI.Runs;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Workflows.GetRun;

/// <summary>
/// Reads the current state of a run the caller started.
/// </summary>
/// <remarks>
/// <see cref="OwnerId"/> is required rather than optional: the store answers as though another
/// owner's run does not exist, and it can only do that if it is told who is asking. A query that
/// omitted the caller would read anyone's run.
/// </remarks>
public sealed record GetWorkflowRunQuery : IRequest<Result<RunRecord>>
{
    /// <summary>Identifier of the workflow the run belongs to.</summary>
    public required Guid WorkflowId { get; init; }

    /// <summary>Identifier of the run to read.</summary>
    public required string JobId { get; init; }

    /// <summary>Stable identity of the calling principal, resolved from its token.</summary>
    public required string OwnerId { get; init; }
}

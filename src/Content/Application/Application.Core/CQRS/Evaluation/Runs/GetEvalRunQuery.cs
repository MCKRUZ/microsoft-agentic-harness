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

/// <summary>
/// One evaluation run as its owner sees it: the substrate's record, what it was asked to evaluate, and
/// the report if it produced one.
/// </summary>
/// <remarks>
/// Three pieces rather than one because they are stored in three places for good reasons — the record
/// is kind-agnostic, the request and report belong to this kind alone. Joining them here rather than at
/// the transport keeps the "a run with no submission is still a readable run" rule in one place instead
/// of in every surface that reads one.
/// </remarks>
public sealed record EvalRunView
{
    /// <summary>The run's identity, ownership and lifecycle.</summary>
    public required RunRecord Run { get; init; }

    /// <summary>
    /// The datasets the run was asked to evaluate. Empty when the submission has already been
    /// reclaimed, which is possible for a run whose own record has not been swept yet.
    /// </summary>
    public IReadOnlyList<string> DatasetNames { get; init; } = [];

    /// <summary>The report, once the run has produced one.</summary>
    public EvalRunReport? Report { get; init; }
}

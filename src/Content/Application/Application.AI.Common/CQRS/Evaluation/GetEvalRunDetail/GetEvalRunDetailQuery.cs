using Domain.AI.Evaluation;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Evaluation.GetEvalRunDetail;

/// <summary>
/// Returns the full <see cref="EvalRunReport"/> for one previously-ingested run.
/// </summary>
/// <remarks>
/// Failure modes:
/// <list type="bullet">
///   <item><description><see cref="Result{T}.NotFound"/> when the run is unknown to the store.</description></item>
///   <item><description><see cref="Result{T}.ValidationFailure"/> when <see cref="RunId"/> is empty.</description></item>
/// </list>
/// </remarks>
public sealed record GetEvalRunDetailQuery : IRequest<Result<EvalRunReport>>
{
    /// <summary>The run identifier to load.</summary>
    public required string RunId { get; init; }
}

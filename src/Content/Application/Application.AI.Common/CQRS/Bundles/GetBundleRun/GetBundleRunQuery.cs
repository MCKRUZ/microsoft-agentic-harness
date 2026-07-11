using Domain.AI.Bundles;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Bundles.GetBundleRun;

/// <summary>
/// Reads the current state of a bundle run. The application-level entry point for
/// <c>GET /api/bundles/{handle}/runs/{jobId}</c> — callers poll it until the run reaches a terminal status.
/// </summary>
/// <remarks>
/// The query is scoped by both <see cref="Handle"/> and <see cref="JobId"/>: a run is only readable under
/// the handle it belongs to, so a caller cannot read another handle's run by guessing its job id (the run
/// is reported as not found rather than revealing it exists under a different handle).
/// </remarks>
public sealed record GetBundleRunQuery : IRequest<Result<BundleRunRecord>>
{
    /// <summary>The handle the run belongs to.</summary>
    public required string Handle { get; init; }

    /// <summary>The job id of the run to read.</summary>
    public required string JobId { get; init; }
}

using Domain.AI.DriftDetection;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Lists the active drift baselines, optionally filtered to one scope level. Baselines are
/// operational quality snapshots with no per-caller visibility rules, so the read is gated by
/// role alone (<c>Harness.Drift.Read</c>) at the HTTP surface.
/// </summary>
public sealed record GetDriftBaselinesQuery : IRequest<Result<IReadOnlyList<DriftBaseline>>>
{
    /// <summary>Optional scope filter. Null returns baselines across all scope levels.</summary>
    public DriftScope? Scope { get; init; }
}
